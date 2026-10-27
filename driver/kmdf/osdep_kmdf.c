/*
 * Copyright (c) 2026 Nitrobyte / airdrop-win contributors
 *
 * Permission to use, copy, modify, and distribute this software for any
 * purpose with or without fee is hereby granted, provided that the above
 * copyright notice and this permission notice appear in all copies.
 *
 * THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 * WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 * ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 * WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 * ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 * OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
 */

/*
 * osdep.h implementation for KMDF.
 *
 * HVCI notes: no executable memory is ever allocated. Pool comes from
 * ExAllocatePool2 (POOL_FLAG_NON_PAGED is NX), DMA memory from KMDF
 * common buffers (NX, logical addresses from the DMA adapter, so it works
 * behind the IOMMU / Kernel DMA Protection), MMIO via MmMapIoSpaceEx with
 * PAGE_READWRITE | PAGE_NOCACHE.
 *
 * Firmware location: the INF copies the .ucode next to the .sys in the
 * driver store (DIRID 13). It is opened relative to the directory returned
 * by IoGetDriverDirectory(DriverDirectoryImage) - the documented way for
 * a package-isolated driver to find its own files. Fallback:
 * \SystemRoot\System32\drivers\airdrop-win\<name> (manual installs).
 */

#include "airdrop.h"
#include <ntstrsafe.h>

#define CTX(os)	((PDEVICE_CONTEXT)(os))

uint32_t
osdep_read32(void *os, uint32_t off)
{
	return READ_REGISTER_ULONG((volatile ULONG *)(CTX(os)->Bar0 + off));
}

void
osdep_write32(void *os, uint32_t off, uint32_t val)
{
	WRITE_REGISTER_ULONG((volatile ULONG *)(CTX(os)->Bar0 + off), val);
}

void
osdep_write8(void *os, uint32_t off, uint8_t val)
{
	WRITE_REGISTER_UCHAR((volatile UCHAR *)(CTX(os)->Bar0 + off), val);
}

void
osdep_barrier(void *os)
{
	UNREFERENCED_PARAMETER(os);
	KeMemoryBarrier();
}

uint32_t
osdep_pci_read32(void *os, uint32_t off)
{
	PDEVICE_CONTEXT ctx = CTX(os);
	ULONG v = 0xffffffff;

	if (ctx->BusValid)
		(void)ctx->Bus.GetBusData(ctx->Bus.Context,
		    PCI_WHICH_SPACE_CONFIG, &v, off, sizeof(v));
	return v;
}

void
osdep_pci_write32(void *os, uint32_t off, uint32_t val)
{
	PDEVICE_CONTEXT ctx = CTX(os);
	ULONG v = val;

	if (ctx->BusValid)
		(void)ctx->Bus.SetBusData(ctx->Bus.Context,
		    PCI_WHICH_SPACE_CONFIG, &v, off, sizeof(v));
}

void
osdep_delay_us(uint32_t us)
{
	/*
	 * iwx DELAY() semantics. Long delays at PASSIVE_LEVEL sleep instead
	 * of spinning (iwx_prepare_card_hw can wait 25 ms at a time).
	 */
	if (us >= 1000 && KeGetCurrentIrql() == PASSIVE_LEVEL) {
		LARGE_INTEGER t;
		t.QuadPart = -(LONGLONG)us * 10;
		(void)KeDelayExecutionThread(KernelMode, FALSE, &t);
		return;
	}
	while (us > 0) {
		uint32_t chunk = us > 50 ? 50 : us;
		KeStallExecutionProcessor(chunk);
		us -= chunk;
	}
}

uint64_t
osdep_time_us(void)
{
	return KeQueryInterruptTimePrecise(NULL) / 10;
}

void *
osdep_malloc(size_t size)
{
	/* zero-initialized, NX non-paged */
	return ExAllocatePool2(POOL_FLAG_NON_PAGED, size ? size : 1,
	    AIRDROP_POOL_TAG);
}

void
osdep_free(void *p, size_t size)
{
	UNREFERENCED_PARAMETER(size);
	if (p != NULL)
		ExFreePoolWithTag(p, AIRDROP_POOL_TAG);
}

int
osdep_dma_alloc(void *os, struct iwx_dma_info *dma, size_t size, size_t align)
{
	PDEVICE_CONTEXT ctx = CTX(os);
	WDF_COMMON_BUFFER_CONFIG cfg;
	WDFCOMMONBUFFER cb;
	NTSTATUS status;
	ULONG req;

	if (KeGetCurrentIrql() != PASSIVE_LEVEL || size == 0)
		return EINVAL;

	/* AlignmentRequirement is "alignment - 1" (FILE_xxx_ALIGNMENT). */
	if (align > PAGE_SIZE)
		return EINVAL;
	req = align > 1 ? (ULONG)(align - 1) : 0;
	WDF_COMMON_BUFFER_CONFIG_INIT(&cfg, req);

	status = WdfCommonBufferCreateWithConfig(ctx->DmaEnabler, size, &cfg,
	    WDF_NO_OBJECT_ATTRIBUTES, &cb);
	if (!NT_SUCCESS(status))
		return ENOMEM;

	dma->os_handle = cb;
	dma->vaddr = WdfCommonBufferGetAlignedVirtualAddress(cb);
	dma->paddr = (uint64_t)WdfCommonBufferGetAlignedLogicalAddress(cb).QuadPart;
	dma->size = size;
	RtlZeroMemory(dma->vaddr, size);
	return 0;
}

void
osdep_dma_free(void *os, struct iwx_dma_info *dma)
{
	UNREFERENCED_PARAMETER(os);
	if (dma->os_handle != NULL)
		WdfObjectDelete((WDFCOMMONBUFFER)dma->os_handle);
	dma->os_handle = NULL;
	dma->vaddr = NULL;
	dma->paddr = 0;
	dma->size = 0;
}

void
osdep_lock(void *os)
{
	WdfSpinLockAcquire(CTX(os)->CoreSpin);
}

void
osdep_unlock(void *os)
{
	WdfSpinLockRelease(CTX(os)->CoreSpin);
}

void
osdep_event_signal(void *os, enum osdep_event_id ev)
{
	if ((int)ev >= 0 && ev < OSDEP_EV_COUNT)
		(void)KeSetEvent(&CTX(os)->Events[ev], IO_NO_INCREMENT, FALSE);
}

void
osdep_event_clear(void *os, enum osdep_event_id ev)
{
	if ((int)ev >= 0 && ev < OSDEP_EV_COUNT)
		KeClearEvent(&CTX(os)->Events[ev]);
}

int
osdep_event_wait(void *os, enum osdep_event_id ev, uint32_t timeout_ms)
{
	LARGE_INTEGER t;
	NTSTATUS status;

	if ((int)ev < 0 || ev >= OSDEP_EV_COUNT)
		return EINVAL;
	t.QuadPart = -(LONGLONG)timeout_ms * 10000;
	status = KeWaitForSingleObject(&CTX(os)->Events[ev], Executive,
	    KernelMode, FALSE, &t);
	return status == STATUS_SUCCESS ? 0 : ETIMEDOUT;
}

void
osdep_log(void *os, enum osdep_log_level lvl, const char *fmt, ...)
{
	static const ULONG map[] = {
		DPFLTR_ERROR_LEVEL, DPFLTR_WARNING_LEVEL,
		DPFLTR_INFO_LEVEL, DPFLTR_TRACE_LEVEL
	};
	char buf[256];
	va_list ap;

	UNREFERENCED_PARAMETER(os);
	va_start(ap, fmt);
	(void)RtlStringCbVPrintfA(buf, sizeof(buf), fmt, ap);
	va_end(ap);
	DbgPrintEx(DPFLTR_IHVDRIVER_ID,
	    map[(unsigned)lvl < RTL_NUMBER_OF(map) ? lvl : OSDEP_LOG_DEBUG],
	    "airdrop: %s", buf);
}

static NTSTATUS
AirdropOpenFile(HANDLE root, PCUNICODE_STRING name, PHANDLE file)
{
	OBJECT_ATTRIBUTES oa;
	IO_STATUS_BLOCK iosb;

	InitializeObjectAttributes(&oa, (PUNICODE_STRING)name,
	    OBJ_KERNEL_HANDLE | OBJ_CASE_INSENSITIVE, root, NULL);
	return ZwCreateFile(file, GENERIC_READ | SYNCHRONIZE, &oa, &iosb, NULL,
	    FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_OPEN,
	    FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE, NULL, 0);
}

int
osdep_fw_load(void *os, const char *name, uint8_t **data, size_t *len)
{
	PDEVICE_CONTEXT ctx = CTX(os);
	WCHAR wname[64], wpath[160];
	UNICODE_STRING uname, upath;
	FILE_STANDARD_INFORMATION fsi;
	IO_STATUS_BLOCK iosb;
	HANDLE dir = NULL, file = NULL;
	NTSTATUS status;
	uint8_t *buf = NULL;
	size_t i, n;
	ULONG size;

	*data = NULL;
	*len = 0;
	if (name == NULL || KeGetCurrentIrql() != PASSIVE_LEVEL)
		return EINVAL;

	/* plain file name only, ASCII -> UTF-16 */
	n = strlen(name);
	if (n == 0 || n >= RTL_NUMBER_OF(wname))
		return EINVAL;
	for (i = 0; i < n; i++) {
		if (name[i] == '\\' || name[i] == '/' || name[i] == ':' ||
		    (unsigned char)name[i] >= 0x80)
			return EINVAL;
		wname[i] = (WCHAR)name[i];
	}
	wname[n] = L'\0';
	RtlInitUnicodeString(&uname, wname);

	/* 1. driver store directory of this driver image */
	status = IoGetDriverDirectory(
	    WdfDriverWdmGetDriverObject(WdfGetDriver()),
	    DriverDirectoryImage, 0, &dir);
	if (NT_SUCCESS(status)) {
		status = AirdropOpenFile(dir, &uname, &file);
		ZwClose(dir);
	}

	/* 2. fallback for manual installs */
	if (!NT_SUCCESS(status)) {
		status = RtlStringCbPrintfW(wpath, sizeof(wpath),
		    L"\\SystemRoot\\System32\\drivers\\airdrop-win\\%ws", wname);
		if (NT_SUCCESS(status)) {
			RtlInitUnicodeString(&upath, wpath);
			status = AirdropOpenFile(NULL, &upath, &file);
		}
	}
	if (!NT_SUCCESS(status)) {
		osdep_log(ctx, OSDEP_LOG_ERR, "firmware %s not found (0x%08x)\n",
		    name, status);
		return ENOENT;
	}

	status = ZwQueryInformationFile(file, &iosb, &fsi, sizeof(fsi),
	    FileStandardInformation);
	if (!NT_SUCCESS(status) || fsi.EndOfFile.QuadPart <= 0 ||
	    fsi.EndOfFile.QuadPart > AIRDROP_FW_MAX_SIZE) {
		ZwClose(file);
		return EINVAL;
	}
	size = (ULONG)fsi.EndOfFile.QuadPart;

	/* only touched at PASSIVE_LEVEL (parse, DMA copy): paged is fine */
	buf = ExAllocatePool2(POOL_FLAG_PAGED, size, AIRDROP_POOL_TAG);
	if (buf == NULL) {
		ZwClose(file);
		return ENOMEM;
	}
	status = ZwReadFile(file, NULL, NULL, NULL, &iosb, buf, size, NULL,
	    NULL);
	ZwClose(file);
	if (!NT_SUCCESS(status) || iosb.Information != size) {
		ExFreePoolWithTag(buf, AIRDROP_POOL_TAG);
		return EIO;
	}

	*data = buf;
	*len = size;
	return 0;
}

void
osdep_fw_release(void *os, uint8_t *data, size_t len)
{
	UNREFERENCED_PARAMETER(os);
	UNREFERENCED_PARAMETER(len);
	if (data != NULL)
		ExFreePoolWithTag(data, AIRDROP_POOL_TAG);
}

/*
 * DISPATCH_LEVEL, CoreSpin held. Copy into the RX ring; pending
 * READ_FRAME requests are completed from the DPC afterwards
 * (AirdropRxDeliverPending) so no request completes under CoreSpin.
 */
void
osdep_rx_frame(void *os, const struct iwx_rx_meta *meta,
    const uint8_t *frame, uint32_t len)
{
	PDEVICE_CONTEXT ctx = CTX(os);
	AIRDROP_RX_SLOT *slot;
	ULONG copy;

	WdfSpinLockAcquire(ctx->RxLock);
	if (ctx->RxRing == NULL || ctx->RxCount >= AIRDROP_RX_RING_SLOTS) {
		ctx->RxDroppedPending++;
		InterlockedIncrement(&ctx->RxDroppedTotal);
		WdfSpinLockRelease(ctx->RxLock);
		return;
	}
	slot = &ctx->RxRing[ctx->RxHead];
	copy = len > AIRDROP_RX_SLOT_DATA ? AIRDROP_RX_SLOT_DATA : len;

	RtlZeroMemory(&slot->Hdr, sizeof(slot->Hdr));
	slot->Hdr.HeaderSize = sizeof(AIRDROP_RX_HEADER);
	slot->Hdr.FrameLength = copy;
	slot->Hdr.OriginalLength = len;
	slot->Hdr.HostTimestamp = KeQueryInterruptTimePrecise(NULL);
	slot->Hdr.Tsf = meta->tsf;
	slot->Hdr.DeviceTimestampUs = meta->gp2;
	slot->Hdr.RateNFlags = meta->rate_n_flags;
	slot->Hdr.RssiDbm = meta->rssi_dbm;
	slot->Hdr.Channel = meta->channel;
	if (meta->flags & IWX_RXMETA_F_SHORTPRE)
		slot->Hdr.Flags |= AIRDROP_RX_F_SHORT_PREAMBLE;
	if (meta->flags & IWX_RXMETA_F_TSF_VALID)
		slot->Hdr.Flags |= AIRDROP_RX_F_TSF_VALID;
	if (copy < len)
		slot->Hdr.Flags |= AIRDROP_RX_F_TRUNCATED;
	slot->Hdr.DroppedSinceLast = ctx->RxDroppedPending;
	ctx->RxDroppedPending = 0;
	RtlCopyMemory(slot->Data, frame, copy);

	ctx->RxHead = (ctx->RxHead + 1) % AIRDROP_RX_RING_SLOTS;
	ctx->RxCount++;
	WdfSpinLockRelease(ctx->RxLock);
}

void
osdep_fatal(void *os, enum osdep_fatal_reason reason)
{
	AirdropQueueFatal(CTX(os), (int)reason);
}
