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
 * DriverEntry, PnP/power callbacks, interrupt and DMA setup.
 *
 * Power/PnP sequence:
 *   EvtDeviceAdd          queues, locks, DMA enabler, interrupt object,
 *                         device interface.
 *   PrepareHardware       map BAR0 (MmMapIoSpaceEx, uncached, RW),
 *                         PCI bus interface, iwx_attach() (DMA common
 *                         buffers for context info, rings, ICT).
 *   D0Entry               nothing (device is reset by iwx itself).
 *   D0EntryPostIntEnabled MSI-X detection, iwx_preinit(): first firmware
 *                         boot, NVM read (MAC, channels), device stopped.
 *   D0ExitPreIntDisabled  radio off, iwx_stop(), pending reads failed.
 *   ReleaseHardware       iwx_detach() (all common buffers), unmap BAR0.
 * Nothing survives ReleaseHardware, so a PnP remove / driver update leaves
 * the hardware in the state iwx_stop_device() leaves it in (APM stopped,
 * SW reset), which is what the Intel driver expects on its own start.
 */

#include "airdrop.h"

DRIVER_INITIALIZE DriverEntry;
static EVT_WDF_DEVICE_PREPARE_HARDWARE		AirdropEvtPrepareHardware;
static EVT_WDF_DEVICE_RELEASE_HARDWARE		AirdropEvtReleaseHardware;
static EVT_WDF_DEVICE_D0_ENTRY_POST_INTERRUPTS_ENABLED AirdropEvtD0EntryPostInterruptsEnabled;
static EVT_WDF_DEVICE_D0_EXIT_PRE_INTERRUPTS_DISABLED AirdropEvtD0ExitPreInterruptsDisabled;
static EVT_WDF_INTERRUPT_ISR			AirdropEvtIsr;
static EVT_WDF_INTERRUPT_DPC			AirdropEvtDpc;
static EVT_WDF_WORKITEM				AirdropEvtFatalWork;

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, AirdropEvtDeviceAdd)
#pragma alloc_text(PAGE, AirdropEvtPrepareHardware)
#pragma alloc_text(PAGE, AirdropEvtReleaseHardware)
#pragma alloc_text(PAGE, AirdropEvtD0EntryPostInterruptsEnabled)
#pragma alloc_text(PAGE, AirdropEvtD0ExitPreInterruptsDisabled)
#pragma alloc_text(PAGE, AirdropEvtFatalWork)
#endif

NTSTATUS
AirdropErrnoToStatus(int err)
{
	switch (err) {
	case 0:		return STATUS_SUCCESS;
	case ENOMEM:	return STATUS_INSUFFICIENT_RESOURCES;
	case ETIMEDOUT:	return STATUS_IO_TIMEOUT;
	case EINVAL:	return STATUS_INVALID_PARAMETER;
	case ENOTSUP:	return STATUS_NOT_IMPLEMENTED;
	case EBUSY:	return STATUS_DEVICE_BUSY;
	case ENXIO:	return STATUS_DEVICE_NOT_READY;
	case EPERM:	return STATUS_POWER_STATE_INVALID; /* rfkill switch */
	case ENOENT:	return STATUS_OBJECT_NAME_NOT_FOUND;
	case ENOSPC:	return STATUS_DEVICE_BUSY;
	case EIO:	return STATUS_DEVICE_HARDWARE_ERROR;
	default:	return STATUS_UNSUCCESSFUL;
	}
}

NTSTATUS
DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
	WDF_DRIVER_CONFIG config;
	NTSTATUS status;

	/* NX non-paged pool for any legacy ExAllocatePool* use */
	ExInitializeDriverRuntime(DrvRtPoolNxOptIn);

	WDF_DRIVER_CONFIG_INIT(&config, AirdropEvtDeviceAdd);
	config.DriverPoolTag = AIRDROP_POOL_TAG;

	status = WdfDriverCreate(DriverObject, RegistryPath,
	    WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
	return status;
}

NTSTATUS
AirdropEvtDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
	WDF_PNPPOWER_EVENT_CALLBACKS pnp;
	WDF_OBJECT_ATTRIBUTES attr;
	WDF_FILEOBJECT_CONFIG fileConfig;
	WDF_DMA_ENABLER_CONFIG dmaConfig;
	WDF_INTERRUPT_CONFIG intConfig;
	WDF_WORKITEM_CONFIG wiConfig;
	PDEVICE_CONTEXT ctx;
	WDFDEVICE device;
	NTSTATUS status;
	int i;

	UNREFERENCED_PARAMETER(Driver);
	PAGED_CODE();

	WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
	pnp.EvtDevicePrepareHardware = AirdropEvtPrepareHardware;
	pnp.EvtDeviceReleaseHardware = AirdropEvtReleaseHardware;
	pnp.EvtDeviceD0EntryPostInterruptsEnabled =
	    AirdropEvtD0EntryPostInterruptsEnabled;
	pnp.EvtDeviceD0ExitPreInterruptsDisabled =
	    AirdropEvtD0ExitPreInterruptsDisabled;
	WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnp);

	WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_AIRDROP);
	WdfDeviceInitSetCharacteristics(DeviceInit, FILE_DEVICE_SECURE_OPEN, TRUE);
	WdfDeviceInitSetExclusive(DeviceInit, TRUE);
	WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoDirect);

	WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, WDF_NO_EVENT_CALLBACK,
	    WDF_NO_EVENT_CALLBACK, AirdropEvtFileCleanup);
	WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig,
	    WDF_NO_OBJECT_ATTRIBUTES);

	WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attr, DEVICE_CONTEXT);
	status = WdfDeviceCreate(&DeviceInit, &attr, &device);
	if (!NT_SUCCESS(status))
		return status;

	ctx = AirdropGetContext(device);
	ctx->Device = device;
	ctx->InitStatus = STATUS_DEVICE_NOT_READY;
	for (i = 0; i < OSDEP_EV_COUNT; i++)
		KeInitializeEvent(&ctx->Events[i], NotificationEvent, FALSE);

	WDF_OBJECT_ATTRIBUTES_INIT(&attr);
	attr.ParentObject = device;
	status = WdfWaitLockCreate(&attr, &ctx->CoreLock);
	if (!NT_SUCCESS(status))
		return status;
	status = WdfSpinLockCreate(&attr, &ctx->CoreSpin);
	if (!NT_SUCCESS(status))
		return status;
	status = WdfSpinLockCreate(&attr, &ctx->RxLock);
	if (!NT_SUCCESS(status))
		return status;

	/* 64-bit DMA, common buffers only; the device is a bus master. */
	WdfDeviceSetAlignmentRequirement(device, FILE_256_BYTE_ALIGNMENT);
	WDF_DMA_ENABLER_CONFIG_INIT(&dmaConfig, WdfDmaProfileScatterGather64,
	    64 * 1024);
	status = WdfDmaEnablerCreate(device, &dmaConfig,
	    WDF_NO_OBJECT_ATTRIBUTES, &ctx->DmaEnabler);
	if (!NT_SUCCESS(status))
		return status;

	/* One message (INF MessageNumberLimit=1): iwx maps all causes to 0. */
	WDF_INTERRUPT_CONFIG_INIT(&intConfig, AirdropEvtIsr, AirdropEvtDpc);
	status = WdfInterruptCreate(device, &intConfig,
	    WDF_NO_OBJECT_ATTRIBUTES, &ctx->Interrupt);
	if (!NT_SUCCESS(status))
		return status;

	WDF_WORKITEM_CONFIG_INIT(&wiConfig, AirdropEvtFatalWork);
	WDF_OBJECT_ATTRIBUTES_INIT(&attr);
	attr.ParentObject = device;
	status = WdfWorkItemCreate(&wiConfig, &attr, &ctx->FatalWork);
	if (!NT_SUCCESS(status))
		return status;

	status = AirdropQueueInitialize(ctx);
	if (!NT_SUCCESS(status))
		return status;

	return WdfDeviceCreateDeviceInterface(device,
	    &GUID_DEVINTERFACE_AIRDROP_WIFI, NULL);
}

static ULONG
AirdropPciRead32(PDEVICE_CONTEXT ctx, ULONG off)
{
	ULONG v = 0xffffffff;

	if (ctx->BusValid)
		(void)ctx->Bus.GetBusData(ctx->Bus.Context, PCI_WHICH_SPACE_CONFIG,
		    &v, off, sizeof(v));
	return v;
}

static NTSTATUS
AirdropEvtPrepareHardware(_In_ WDFDEVICE Device,
    _In_ WDFCMRESLIST ResourcesRaw, _In_ WDFCMRESLIST ResourcesTranslated)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(Device);
	PCM_PARTIAL_RESOURCE_DESCRIPTOR desc;
	ULONG i, count, id, cmd;
	NTSTATUS status;
	int err;

	UNREFERENCED_PARAMETER(ResourcesRaw);
	PAGED_CODE();

	count = WdfCmResourceListGetCount(ResourcesTranslated);
	for (i = 0; i < count; i++) {
		desc = WdfCmResourceListGetDescriptor(ResourcesTranslated, i);
		if (desc == NULL)
			continue;
		switch (desc->Type) {
		case CmResourceTypeMemory:
			if (ctx->Bar0 == NULL) {	/* BAR0 = first memory BAR */
				ctx->Bar0 = MmMapIoSpaceEx(desc->u.Memory.Start,
				    desc->u.Memory.Length,
				    PAGE_READWRITE | PAGE_NOCACHE);
				if (ctx->Bar0 == NULL)
					return STATUS_INSUFFICIENT_RESOURCES;
				ctx->Bar0Length = desc->u.Memory.Length;
			}
			break;
		case CmResourceTypeInterrupt:
			if (desc->Flags & CM_RESOURCE_INTERRUPT_MESSAGE)
				ctx->MessageSignaled = TRUE;
			break;
		default:
			break;
		}
	}
	if (ctx->Bar0 == NULL || ctx->Bar0Length < 0x2000)
		return STATUS_DEVICE_CONFIGURATION_ERROR;

	status = WdfFdoQueryForInterface(Device, &GUID_BUS_INTERFACE_STANDARD,
	    (PINTERFACE)&ctx->Bus, sizeof(ctx->Bus), 1, NULL);
	if (!NT_SUCCESS(status))
		return status;
	ctx->BusValid = TRUE;

	id = AirdropPciRead32(ctx, 0x00);
	ctx->PciDevice = (USHORT)(id >> 16);
	id = AirdropPciRead32(ctx, 0x2c);
	ctx->PciSubdevice = (USHORT)(id >> 16);

	/*
	 * Assumption: pci.sys enables memory decode, and bus mastering once
	 * the DMA adapter exists (WdfDmaEnablerCreate). Check and log.
	 */
	cmd = AirdropPciRead32(ctx, 0x04);
	if ((cmd & 0x6) != 0x6) {
		KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_WARNING_LEVEL,
		    "airdrop: PCI command 0x%04x lacks MEM/BUSMASTER\n",
		    cmd & 0xffff));
	}

	ctx->Sc = ExAllocatePool2(POOL_FLAG_NON_PAGED, iwx_softc_size(),
	    AIRDROP_POOL_TAG);
	if (ctx->Sc == NULL)
		return STATUS_INSUFFICIENT_RESOURCES;

	ctx->RxRing = ExAllocatePool2(POOL_FLAG_NON_PAGED,
	    sizeof(AIRDROP_RX_SLOT) * AIRDROP_RX_RING_SLOTS, AIRDROP_POOL_TAG);
	if (ctx->RxRing == NULL)
		return STATUS_INSUFFICIENT_RESOURCES;
	ctx->RxHead = ctx->RxCount = 0;

	WdfWaitLockAcquire(ctx->CoreLock, NULL);
	err = iwx_attach(ctx->Sc, ctx, ctx->PciDevice, ctx->PciSubdevice);
	ctx->Attached = (err == 0);
	WdfWaitLockRelease(ctx->CoreLock);
	if (err) {
		KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
		    "airdrop: iwx_attach failed (%d)\n", err));
		return AirdropErrnoToStatus(err);
	}
	return STATUS_SUCCESS;
}

static NTSTATUS
AirdropEvtReleaseHardware(_In_ WDFDEVICE Device,
    _In_ WDFCMRESLIST ResourcesTranslated)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(Device);

	UNREFERENCED_PARAMETER(ResourcesTranslated);
	PAGED_CODE();

	WdfWorkItemFlush(ctx->FatalWork);

	WdfWaitLockAcquire(ctx->CoreLock, NULL);
	if (ctx->Attached) {
		iwx_detach(ctx->Sc);
		ctx->Attached = FALSE;
	}
	WdfWaitLockRelease(ctx->CoreLock);

	if (ctx->Sc != NULL) {
		ExFreePoolWithTag(ctx->Sc, AIRDROP_POOL_TAG);
		ctx->Sc = NULL;
	}
	if (ctx->RxRing != NULL) {
		AirdropRxFlush(ctx, STATUS_DEVICE_REMOVED);
		ExFreePoolWithTag(ctx->RxRing, AIRDROP_POOL_TAG);
		ctx->RxRing = NULL;
	}
	if (ctx->BusValid) {
		ctx->Bus.InterfaceDereference(ctx->Bus.Context);
		ctx->BusValid = FALSE;
	}
	if (ctx->Bar0 != NULL) {
		MmUnmapIoSpace(ctx->Bar0, ctx->Bar0Length);
		ctx->Bar0 = NULL;
		ctx->Bar0Length = 0;
	}
	return STATUS_SUCCESS;
}

/* MSI-X capability (ID 0x11) present and enabled by pci.sys? */
static BOOLEAN
AirdropMsixEnabled(PDEVICE_CONTEXT ctx)
{
	ULONG reg, off;
	int n;

	reg = AirdropPciRead32(ctx, 0x04);
	if (((reg >> 16) & 0x0010) == 0)
		return FALSE;
	off = AirdropPciRead32(ctx, 0x34) & 0xfc;
	for (n = 0; off >= 0x40 && n < 48; n++) {
		reg = AirdropPciRead32(ctx, off);
		if ((reg & 0xff) == 0x11)
			return (reg & 0x80000000u) ? TRUE : FALSE;
		off = (reg >> 8) & 0xfc;
	}
	return FALSE;
}

static NTSTATUS
AirdropEvtD0EntryPostInterruptsEnabled(_In_ WDFDEVICE Device,
    _In_ WDF_POWER_DEVICE_STATE PreviousState)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(Device);
	int err;

	UNREFERENCED_PARAMETER(PreviousState);
	PAGED_CODE();

	ctx->Msix = ctx->MessageSignaled && AirdropMsixEnabled(ctx);

	WdfWaitLockAcquire(ctx->CoreLock, NULL);
	ctx->InD0 = TRUE;
	if (ctx->Attached) {
		iwx_set_msix(ctx->Sc, ctx->Msix);
		err = iwx_preinit(ctx->Sc);
		ctx->InitStatus = AirdropErrnoToStatus(err);
	} else {
		ctx->InitStatus = STATUS_DEVICE_NOT_READY;
	}
	WdfWaitLockRelease(ctx->CoreLock);

	/*
	 * A failed firmware boot does not fail D0: the device stays visible
	 * so GET_INFO can report InitStatus and the debug log can be read.
	 * START retries the full bring-up.
	 */
	if (!NT_SUCCESS(ctx->InitStatus)) {
		KdPrintEx((DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
		    "airdrop: preinit failed 0x%08x\n", ctx->InitStatus));
	}
	return STATUS_SUCCESS;
}

static NTSTATUS
AirdropEvtD0ExitPreInterruptsDisabled(_In_ WDFDEVICE Device,
    _In_ WDF_POWER_DEVICE_STATE TargetState)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(Device);

	UNREFERENCED_PARAMETER(TargetState);
	PAGED_CODE();

	WdfWorkItemFlush(ctx->FatalWork);

	WdfWaitLockAcquire(ctx->CoreLock, NULL);
	if (ctx->Attached)
		AirdropRadioDownLocked(ctx);
	ctx->InD0 = FALSE;
	WdfWaitLockRelease(ctx->CoreLock);

	AirdropRxFlush(ctx, STATUS_DEVICE_POWERED_OFF);
	return STATUS_SUCCESS;
}

/* DIRQL: MMIO only. */
static BOOLEAN
AirdropEvtIsr(_In_ WDFINTERRUPT Interrupt, _In_ ULONG MessageID)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(WdfInterruptGetDevice(Interrupt));
	uint32_t fh, hw;

	UNREFERENCED_PARAMETER(MessageID);

	if (!ctx->Attached || ctx->Bar0 == NULL)
		return FALSE;

	if (ctx->Msix) {
		iwx_isr_msix(ctx->Sc, &fh, &hw);
		/*
		 * Queue the DPC even with zero causes: it must clear the
		 * MSI-X automask bit or the vector stays masked.
		 */
		InterlockedOr(&ctx->PendingFh, (LONG)fh);
		InterlockedOr(&ctx->PendingHw, (LONG)hw);
		WdfInterruptQueueDpcForIsr(Interrupt);
		return TRUE;
	}

	if (!iwx_isr_legacy(ctx->Sc))
		return FALSE;	/* shared INTx line, not ours */
	WdfInterruptQueueDpcForIsr(Interrupt);
	return TRUE;
}

static VOID
AirdropEvtDpc(_In_ WDFINTERRUPT Interrupt, _In_ WDFOBJECT AssociatedObject)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(WdfInterruptGetDevice(Interrupt));

	UNREFERENCED_PARAMETER(AssociatedObject);

	if (!ctx->Attached)
		return;

	if (ctx->Msix) {
		uint32_t fh = (uint32_t)InterlockedExchange(&ctx->PendingFh, 0);
		uint32_t hw = (uint32_t)InterlockedExchange(&ctx->PendingHw, 0);
		iwx_dpc_msix(ctx->Sc, fh, hw);
	} else {
		iwx_dpc_legacy(ctx->Sc);
	}

	AirdropRxDeliverPending(ctx);
}

/* PASSIVE: firmware assert, hardware error, rfkill change, CT kill. */
static VOID
AirdropEvtFatalWork(_In_ WDFWORKITEM WorkItem)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(WdfWorkItemGetParentObject(WorkItem));
	LONG reasons;

	PAGED_CODE();

	reasons = InterlockedExchange(&ctx->FatalReasons, 0);
	if (reasons == 0)
		return;

	WdfWaitLockAcquire(ctx->CoreLock, NULL);
	if (ctx->Attached && ctx->InD0) {
		if (reasons & (1 << OSDEP_FATAL_FW_ERROR))
			iwx_dump_error_log(ctx->Sc);
		if (reasons & ((1 << OSDEP_FATAL_FW_ERROR) |
		    (1 << OSDEP_FATAL_HW_ERROR) | (1 << OSDEP_FATAL_CT_KILL))) {
			AirdropRadioDownLocked(ctx);
			ctx->InitStatus = STATUS_DEVICE_HARDWARE_ERROR;
		} else if (reasons & (1 << OSDEP_FATAL_RFKILL)) {
			struct iwx_info info;
			iwx_get_info(ctx->Sc, &info);
			/* radio switched off while running: stop (iwx too) */
			if (info.rfkill && info.state != 0)
				AirdropRadioDownLocked(ctx);
		}
	}
	WdfWaitLockRelease(ctx->CoreLock);

	AirdropRxFlush(ctx, STATUS_DEVICE_HARDWARE_ERROR);
}

/* Called by osdep_fatal() at DISPATCH_LEVEL. */
VOID
AirdropQueueFatal(_In_ PDEVICE_CONTEXT ctx, _In_ int reason)
{
	InterlockedOr(&ctx->FatalReasons, 1 << reason);
	WdfWorkItemEnqueue(ctx->FatalWork);
}
