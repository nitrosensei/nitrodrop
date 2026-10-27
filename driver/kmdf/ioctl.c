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
 * IOCTL dispatch (driver/include/airdrop_ioctl.h).
 *
 * Default queue: sequential, not power managed (the radio is only up
 * while the device is in D0 anyway; requests check InD0). READ_FRAME
 * requests are forwarded to a manual queue and completed from the DPC
 * once frames are in the RX ring.
 */

#include <initguid.h>	/* instantiates GUID_DEVINTERFACE_AIRDROP_WIFI */
#include "airdrop.h"

static EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL AirdropEvtIoDeviceControl;

#ifdef ALLOC_PRAGMA
#pragma alloc_text(PAGE, AirdropQueueInitialize)
#endif

NTSTATUS
AirdropQueueInitialize(_In_ PDEVICE_CONTEXT Ctx)
{
	WDF_IO_QUEUE_CONFIG qc;
	NTSTATUS status;

	PAGED_CODE();

	WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&qc, WdfIoQueueDispatchSequential);
	qc.EvtIoDeviceControl = AirdropEvtIoDeviceControl;
	qc.PowerManaged = WdfFalse;
	status = WdfIoQueueCreate(Ctx->Device, &qc, WDF_NO_OBJECT_ATTRIBUTES,
	    WDF_NO_HANDLE);
	if (!NT_SUCCESS(status))
		return status;

	WDF_IO_QUEUE_CONFIG_INIT(&qc, WdfIoQueueDispatchManual);
	qc.PowerManaged = WdfFalse;
	return WdfIoQueueCreate(Ctx->Device, &qc, WDF_NO_OBJECT_ATTRIBUTES,
	    &Ctx->ReadQueue);
}

/* Copy the oldest RX slot into a READ_FRAME request. RxLock held. */
static NTSTATUS
AirdropRxCopyOut(_In_ PDEVICE_CONTEXT Ctx, _In_ WDFREQUEST Request,
    _Out_ size_t *Written)
{
	AIRDROP_RX_SLOT *slot;
	AIRDROP_RX_HEADER hdr;
	PUCHAR out;
	size_t outLen, room;
	NTSTATUS status;
	ULONG tail;

	*Written = 0;
	status = WdfRequestRetrieveOutputBuffer(Request,
	    sizeof(AIRDROP_RX_HEADER), (PVOID *)&out, &outLen);
	if (!NT_SUCCESS(status))
		return status;

	tail = (Ctx->RxHead + AIRDROP_RX_RING_SLOTS - Ctx->RxCount) %
	    AIRDROP_RX_RING_SLOTS;
	slot = &Ctx->RxRing[tail];
	hdr = slot->Hdr;
	room = outLen - sizeof(hdr);
	if (hdr.FrameLength > room) {
		hdr.FrameLength = (UINT32)room;
		hdr.Flags |= AIRDROP_RX_F_TRUNCATED;
	}
	RtlCopyMemory(out, &hdr, sizeof(hdr));
	RtlCopyMemory(out + sizeof(hdr), slot->Data, hdr.FrameLength);
	Ctx->RxCount--;
	*Written = sizeof(hdr) + hdr.FrameLength;
	return STATUS_SUCCESS;
}

/* DPC / PASSIVE: pair queued frames with pending READ_FRAME requests. */
VOID
AirdropRxDeliverPending(_In_ PDEVICE_CONTEXT Ctx)
{
	WDFREQUEST req;
	NTSTATUS status;
	size_t written;

	for (;;) {
		WdfSpinLockAcquire(Ctx->RxLock);
		if (Ctx->RxRing == NULL || Ctx->RxCount == 0) {
			WdfSpinLockRelease(Ctx->RxLock);
			return;
		}
		status = WdfIoQueueRetrieveNextRequest(Ctx->ReadQueue, &req);
		if (!NT_SUCCESS(status)) {
			WdfSpinLockRelease(Ctx->RxLock);
			return;
		}
		status = AirdropRxCopyOut(Ctx, req, &written);
		WdfSpinLockRelease(Ctx->RxLock);
		WdfRequestCompleteWithInformation(req, status, written);
	}
}

/* Fail all pending reads and drop queued frames. */
VOID
AirdropRxFlush(_In_ PDEVICE_CONTEXT Ctx, _In_ NTSTATUS Status)
{
	WDFREQUEST req;

	WdfSpinLockAcquire(Ctx->RxLock);
	Ctx->RxCount = 0;
	Ctx->RxHead = 0;
	WdfSpinLockRelease(Ctx->RxLock);

	while (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Ctx->ReadQueue, &req)))
		WdfRequestComplete(req, Status);
}

/* CoreLock held. Radio off and device stopped (iwx_stop). */
VOID
AirdropRadioDownLocked(_In_ PDEVICE_CONTEXT Ctx)
{
	struct iwx_info info;

	iwx_get_info(Ctx->Sc, &info);
	if (info.state != 0 /* IWX_STATE_DOWN */) {
		(void)iwx_radio_stop(Ctx->Sc);
		iwx_stop(Ctx->Sc);
	}
}

static VOID
AirdropFillInfo(_In_ PDEVICE_CONTEXT Ctx, _Out_ PAIRDROP_INFO Out)
{
	struct iwx_info info;
	ULONG i;

	RtlZeroMemory(Out, sizeof(*Out));
	Out->Size = sizeof(*Out);
	Out->AbiVersion = AIRDROP_ABI_VERSION;
	Out->InitStatus = Ctx->InitStatus;
	if (!Ctx->Attached) {
		Out->State = AIRDROP_STATE_ERROR;
		return;
	}
	iwx_get_info(Ctx->Sc, &info);
	Out->State = (UINT32)info.state;	/* same numbering */
	RtlCopyMemory(Out->MacAddress, info.macaddr, sizeof(Out->MacAddress));
	Out->RfKill = (UINT8)info.rfkill;
	Out->Msix = (UINT8)info.msix;
	RtlCopyMemory(Out->FirmwareVersion, info.fwver,
	    sizeof(Out->FirmwareVersion));
	Out->FirmwareVersion[sizeof(Out->FirmwareVersion) - 1] = '\0';
	Out->HwRev = info.hw_rev;
	Out->HwRfId = info.hw_rf_id;
	Out->LastFirmwareError = info.last_fw_error;
	Out->RxFrames = info.rx_frames;
	Out->RxDropped = info.rx_dropped + (UINT32)Ctx->RxDroppedTotal;
	for (i = 0; i < (ULONG)info.nchan && i < AIRDROP_MAX_CHANNELS; i++)
		Out->Channels[i] = info.chan[i];
	Out->ChannelCount = i;
}

static NTSTATUS
AirdropIoctlStart(_In_ PDEVICE_CONTEXT Ctx, _In_ WDFREQUEST Request)
{
	PAIRDROP_START_PARAMS p;
	struct iwx_info info;
	size_t len;
	NTSTATUS status;
	int err;

	status = WdfRequestRetrieveInputBuffer(Request, sizeof(*p),
	    (PVOID *)&p, &len);
	if (!NT_SUCCESS(status))
		return status;
	if (p->Size < sizeof(*p) || p->Channel == 0)
		return STATUS_INVALID_PARAMETER;

	WdfWaitLockAcquire(Ctx->CoreLock, NULL);
	if (!Ctx->Attached || !Ctx->InD0) {
		status = STATUS_DEVICE_NOT_READY;
		goto out;
	}
	iwx_get_info(Ctx->Sc, &info);
	if (info.state == AIRDROP_STATE_RADIO_ON) {
		status = STATUS_DEVICE_BUSY;
		goto out;
	}
	if (info.state == AIRDROP_STATE_ERROR) {
		iwx_stop(Ctx->Sc);
		info.state = AIRDROP_STATE_DOWN;
	}
	if (info.state == AIRDROP_STATE_DOWN) {
		err = iwx_init(Ctx->Sc);	/* firmware + iwx_init_hw() */
		if (err) {
			status = AirdropErrnoToStatus(err);
			goto out;
		}
	}
	err = iwx_radio_start(Ctx->Sc, p->Channel, p->Bssid,
	    (p->Flags & AIRDROP_START_F_LISTENER_ONLY) ?
	    IWX_RADIO_F_LISTENER_ONLY : 0);
	status = AirdropErrnoToStatus(err);
 out:
	WdfWaitLockRelease(Ctx->CoreLock);
	return status;
}

static NTSTATUS
AirdropIoctlInject(_In_ PDEVICE_CONTEXT Ctx, _In_ WDFREQUEST Request)
{
	PAIRDROP_TX_HEADER h;
	size_t len;
	NTSTATUS status;
	int err;

	status = WdfRequestRetrieveInputBuffer(Request, sizeof(*h),
	    (PVOID *)&h, &len);
	if (!NT_SUCCESS(status))
		return status;
	if (h->HeaderSize < sizeof(*h) || h->HeaderSize > len ||
	    h->FrameLength == 0 || h->FrameLength > AIRDROP_TX_MAX_FRAME ||
	    h->FrameLength > len - h->HeaderSize)
		return STATUS_INVALID_PARAMETER;

	WdfWaitLockAcquire(Ctx->CoreLock, NULL);
	if (!Ctx->Attached || !Ctx->InD0) {
		WdfWaitLockRelease(Ctx->CoreLock);
		return STATUS_DEVICE_NOT_READY;
	}
	err = iwx_tx_inject(Ctx->Sc, (const uint8_t *)h + h->HeaderSize,
	    h->FrameLength, h->Rate,
	    ((h->Flags & AIRDROP_TX_F_NOACK) ? IWX_TX_F_NOACK : 0) |
	    ((h->Flags & AIRDROP_TX_F_RATE_HT) ? IWX_TX_F_RATE_HT : 0));
	WdfWaitLockRelease(Ctx->CoreLock);
	return AirdropErrnoToStatus(err);
}

static VOID
AirdropEvtIoDeviceControl(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength, _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(WdfIoQueueGetDevice(Queue));
	NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
	size_t info = 0;

	UNREFERENCED_PARAMETER(InputBufferLength);

	switch (IoControlCode) {
	case IOCTL_AIRDROP_GET_INFO: {
		PAIRDROP_INFO out;
		status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*out),
		    (PVOID *)&out, NULL);
		if (!NT_SUCCESS(status))
			break;
		WdfWaitLockAcquire(ctx->CoreLock, NULL);
		AirdropFillInfo(ctx, out);
		WdfWaitLockRelease(ctx->CoreLock);
		info = sizeof(*out);
		break;
	}

	case IOCTL_AIRDROP_START:
		status = AirdropIoctlStart(ctx, Request);
		break;

	case IOCTL_AIRDROP_STOP:
		WdfWaitLockAcquire(ctx->CoreLock, NULL);
		if (ctx->Attached && ctx->InD0)
			AirdropRadioDownLocked(ctx);
		WdfWaitLockRelease(ctx->CoreLock);
		AirdropRxFlush(ctx, STATUS_CANCELLED);
		status = STATUS_SUCCESS;
		break;

	case IOCTL_AIRDROP_READ_FRAME:
		if (OutputBufferLength < sizeof(AIRDROP_RX_HEADER)) {
			status = STATUS_BUFFER_TOO_SMALL;
			break;
		}
		status = WdfRequestForwardToIoQueue(Request, ctx->ReadQueue);
		if (!NT_SUCCESS(status))
			break;
		AirdropRxDeliverPending(ctx);
		return;		/* completed later */

	case IOCTL_AIRDROP_INJECT_FRAME:
		status = AirdropIoctlInject(ctx, Request);
		break;

	default:
		break;
	}

	WdfRequestCompleteWithInformation(Request, status, info);
}

/* Last handle closed: fail its reads and switch the radio off. */
VOID
AirdropEvtFileCleanup(_In_ WDFFILEOBJECT FileObject)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(WdfFileObjectGetDevice(FileObject));
	WDFREQUEST req;

	while (NT_SUCCESS(WdfIoQueueRetrieveRequestByFileObject(ctx->ReadQueue,
	    FileObject, &req)))
		WdfRequestComplete(req, STATUS_CANCELLED);

	WdfWaitLockAcquire(ctx->CoreLock, NULL);
	if (ctx->Attached && ctx->InD0)
		AirdropRadioDownLocked(ctx);
	WdfWaitLockRelease(ctx->CoreLock);
}
