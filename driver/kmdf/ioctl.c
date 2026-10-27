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
 * Default queue: parallel, not power managed (the radio is only up while
 * the device is in D0 anyway; requests check InD0). Every handler that
 * touches the core takes CoreLock, so they are serialized there. The
 * queue is parallel (it was sequential in M1) because an INJECT_FRAME
 * that wants a result stays pending, cancelable, until the firmware
 * reports the transmission; a sequential queue would hold back every
 * other request meanwhile.
 *
 * READ_FRAME requests are forwarded to a manual queue and completed from
 * the DPC once frames are in the RX ring. Pending INJECT_FRAME requests
 * sit in a TX slot (airdrop_queues.h) and are completed by
 * AirdropTxCompleteDone() after the DPC (TX_CMD response) or after any
 * PASSIVE path that flushes frames (STOP, D0Exit, fatal error, cleanup).
 */

#include <initguid.h>	/* instantiates GUID_DEVINTERFACE_AIRDROP_WIFI */
#include "airdrop.h"

static EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL AirdropEvtIoDeviceControl;
static EVT_WDF_REQUEST_CANCEL AirdropEvtTxCancel;

#ifdef ALLOC_PRAGMA
#pragma alloc_text(PAGE, AirdropQueueInitialize)
#endif

NTSTATUS
AirdropQueueInitialize(_In_ PDEVICE_CONTEXT Ctx)
{
	WDF_IO_QUEUE_CONFIG qc;
	NTSTATUS status;

	PAGED_CODE();

	WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&qc, WdfIoQueueDispatchParallel);
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

/* ---- RX ------------------------------------------------------------------ */

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
	unsigned int idx;

	*Written = 0;
	status = WdfRequestRetrieveOutputBuffer(Request,
	    sizeof(AIRDROP_RX_HEADER), (PVOID *)&out, &outLen);
	if (!NT_SUCCESS(status))
		return status;
	if (!AqRxPeek(&Ctx->Rx, &idx))
		return STATUS_NO_MORE_ENTRIES;	/* caller checked; defensive */

	slot = &Ctx->RxRing[idx];
	hdr = slot->Hdr;
	room = outLen - sizeof(hdr);
	if (hdr.FrameLength > room) {
		hdr.FrameLength = (UINT32)room;
		hdr.Flags |= AIRDROP_RX_F_TRUNCATED;
	}
	RtlCopyMemory(out, &hdr, sizeof(hdr));
	RtlCopyMemory(out + sizeof(hdr), slot->Data, hdr.FrameLength);
	AqRxConsume(&Ctx->Rx);
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
	unsigned int idx;

	for (;;) {
		WdfSpinLockAcquire(Ctx->RxLock);
		if (Ctx->RxRing == NULL || !AqRxPeek(&Ctx->Rx, &idx)) {
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
	AqRxReset(&Ctx->Rx);
	WdfSpinLockRelease(Ctx->RxLock);

	while (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Ctx->ReadQueue, &req)))
		WdfRequestComplete(req, Status);
}

/* ---- TX results ----------------------------------------------------------- */

/*
 * Complete every INJECT_FRAME whose status arrived. PASSIVE or DISPATCH,
 * no spinlock held. A request whose cancel routine already started is
 * left to it (WdfRequestUnmarkCancelable() == STATUS_CANCELLED).
 */
VOID
AirdropTxCompleteDone(_In_ PDEVICE_CONTEXT Ctx)
{
	PAIRDROP_TX_RESULT out;
	AQ_TX_RESULT res;
	WDFREQUEST req;
	void *r;
	NTSTATUS status;
	int got;

	for (;;) {
		WdfSpinLockAcquire(Ctx->TxLock);
		got = AqTxTakeDone(&Ctx->Tx, &r, &res);
		WdfSpinLockRelease(Ctx->TxLock);
		if (!got)
			return;
		req = (WDFREQUEST)r;
		if (req == NULL)
			continue;
		if (WdfRequestUnmarkCancelable(req) == STATUS_CANCELLED)
			continue;	/* AirdropEvtTxCancel completes it */
		status = WdfRequestRetrieveOutputBuffer(req, sizeof(*out),
		    (PVOID *)&out, NULL);
		if (!NT_SUCCESS(status)) {
			WdfRequestComplete(req, status);
			continue;
		}
		RtlZeroMemory(out, sizeof(*out));
		out->Size = sizeof(*out);
		out->Status = res.Status;	/* AIRDROP_TXST_* == IWX_TXST_* */
		out->FwStatus = res.FwStatus;
		out->Retries = res.Retries;
		out->RtsRetries = res.RtsRetries;
		out->Sequence = res.Sequence;
		out->InitialRate = res.Rate;
		out->Flags = res.Flags;
		WdfRequestCompleteWithInformation(req, STATUS_SUCCESS,
		    sizeof(*out));
	}
}

/* Cancel routine of a pending INJECT_FRAME (DISPATCH_LEVEL at most). */
static VOID
AirdropEvtTxCancel(_In_ WDFREQUEST Request)
{
	PDEVICE_CONTEXT ctx = AirdropGetContext(
	    WdfIoQueueGetDevice(WdfRequestGetIoQueue(Request)));

	/* the slot (if still there) keeps waiting for its firmware status */
	WdfSpinLockAcquire(ctx->TxLock);
	(void)AqTxForgetRequest(&ctx->Tx, Request);
	WdfSpinLockRelease(ctx->TxLock);
	WdfRequestComplete(Request, STATUS_CANCELLED);
}

/*
 * Complete pending INJECT_FRAME requests with Status: all of them
 * (FileObject NULL, device going away: slots are freed) or those of one
 * handle (cleanup: slots keep waiting for their firmware status).
 */
VOID
AirdropTxCancelAll(_In_ PDEVICE_CONTEXT Ctx, _In_opt_ WDFFILEOBJECT FileObject,
    _In_ NTSTATUS Status)
{
	WDFREQUEST reqs[AQ_TX_SLOTS];
	ULONG n = 0, i;
	void *r;

	WdfSpinLockAcquire(Ctx->TxLock);
	if (FileObject == NULL) {
		while (n < AQ_TX_SLOTS && AqTxTakeAny(&Ctx->Tx, &r))
			reqs[n++] = (WDFREQUEST)r;
	} else {
		for (i = 0; i < AQ_TX_SLOTS; i++) {
			AQ_TX_SLOT *s = &Ctx->Tx.Slot[i];

			if (s->State == AQ_TX_FREE || s->Request == NULL ||
			    WdfRequestGetFileObject((WDFREQUEST)s->Request) !=
			    FileObject)
				continue;
			reqs[n++] = (WDFREQUEST)s->Request;
			s->Request = NULL;
			if (s->State == AQ_TX_DONE)
				AqTxFree(&Ctx->Tx, s);
		}
	}
	WdfSpinLockRelease(Ctx->TxLock);

	for (i = 0; i < n; i++)
		if (WdfRequestUnmarkCancelable(reqs[i]) != STATUS_CANCELLED)
			WdfRequestComplete(reqs[i], Status);
}

/* ---- control ---------------------------------------------------------------- */

/* CoreLock held. Radio off and device stopped (iwx_stop). */
VOID
AirdropRadioDownLocked(_In_ PDEVICE_CONTEXT Ctx)
{
	struct iwx_info info;

	iwx_get_info(Ctx->Sc, &info);
	if (info.state != 0 /* IWX_STATE_DOWN */) {
		(void)iwx_radio_stop(Ctx->Sc);
		iwx_stop(Ctx->Sc);	/* resets queues: leftovers FLUSHED */
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
	WdfSpinLockAcquire(Ctx->RxLock);
	Out->RxDropped = info.rx_dropped + Ctx->Rx.DroppedTotal;
	WdfSpinLockRelease(Ctx->RxLock);
	for (i = 0; i < (ULONG)info.nchan && i < AIRDROP_MAX_CHANNELS; i++)
		Out->Channels[i] = info.chan[i];
	Out->ChannelCount = i;
	Out->RxBadFcs = info.rx_bad_fcs;
	Out->TxSubmitted = info.tx_submitted;
	Out->TxSuccess = info.tx_success;
	Out->TxFailed = info.tx_failed;
	Out->TxFlushed = info.tx_flushed;
	Out->TxPending = info.tx_pending;
	Out->RadioChannel = info.radio_channel;
	Out->RadioMode = info.radio_mode;
}

static NTSTATUS
AirdropIoctlStart(_In_ PDEVICE_CONTEXT Ctx, _In_ WDFREQUEST Request)
{
	AIRDROP_START_PARAMS p;
	struct iwx_radio_params rp;
	struct iwx_info info;
	PVOID in;
	size_t len;
	NTSTATUS status;
	int err;

	status = WdfRequestRetrieveInputBuffer(Request,
	    AIRDROP_START_PARAMS_V1_SIZE, &in, &len);
	if (!NT_SUCCESS(status))
		return status;
	RtlZeroMemory(&p, sizeof(p));
	RtlCopyMemory(&p, in, len < sizeof(p) ? len : sizeof(p));
	if (p.Size < AIRDROP_START_PARAMS_V1_SIZE || p.Size > len ||
	    p.Channel == 0)
		return STATUS_INVALID_PARAMETER;
	if (p.Size < sizeof(p))		/* v1 caller: no v2 fields */
		p.BeaconIntervalTu = 0;

	RtlZeroMemory(&rp, sizeof(rp));
	rp.channel = p.Channel;
	RtlCopyMemory(rp.bssid, p.Bssid, sizeof(rp.bssid));
	rp.beacon_int = p.BeaconIntervalTu;
	rp.flags = p.Flags;	/* same bit values, core rejects unknown ones */

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
	err = iwx_radio_start(Ctx->Sc, &rp);
	status = AirdropErrnoToStatus(err);
 out:
	WdfWaitLockRelease(Ctx->CoreLock);
	return status;
}

/*
 * INJECT_FRAME. Completes the request itself (now, or later from
 * AirdropTxCompleteDone when an AIRDROP_TX_RESULT buffer was given).
 */
static VOID
AirdropIoctlInject(_In_ PDEVICE_CONTEXT Ctx, _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength)
{
	PAIRDROP_TX_HEADER h;
	struct iwx_tx_params tp;
	size_t len;
	NTSTATUS status;
	BOOLEAN wantResult;
	uint32_t cookie = IWX_TX_COOKIE_NONE;
	int err;

	status = WdfRequestRetrieveInputBuffer(Request, sizeof(*h),
	    (PVOID *)&h, &len);
	if (!NT_SUCCESS(status))
		goto complete;
	if (h->HeaderSize < sizeof(*h) || h->HeaderSize > len ||
	    h->FrameLength == 0 || h->FrameLength > AIRDROP_TX_MAX_FRAME ||
	    h->FrameLength > len - h->HeaderSize) {
		status = STATUS_INVALID_PARAMETER;
		goto complete;
	}
	wantResult = OutputBufferLength >= sizeof(AIRDROP_TX_RESULT);

	if (wantResult) {
		WdfSpinLockAcquire(Ctx->TxLock);
		cookie = AqTxAlloc(&Ctx->Tx, Request);
		WdfSpinLockRelease(Ctx->TxLock);
		if (cookie == 0xffffffffu) {
			status = STATUS_DEVICE_BUSY;
			goto complete;
		}
		status = WdfRequestMarkCancelableEx(Request, AirdropEvtTxCancel);
		if (!NT_SUCCESS(status)) {	/* already cancelled */
			WdfSpinLockAcquire(Ctx->TxLock);
			(void)AqTxAbort(&Ctx->Tx, cookie);
			WdfSpinLockRelease(Ctx->TxLock);
			goto complete;
		}
	}

	RtlZeroMemory(&tp, sizeof(tp));
	tp.rate = h->Rate;
	tp.flags = h->Flags & (AIRDROP_TX_F_NOACK | AIRDROP_TX_F_RATE_HT |
	    AIRDROP_TX_F_ASSIGN_SEQ | AIRDROP_TX_F_SGI | AIRDROP_TX_F_HIGH_PRI);
	tp.cookie = cookie;

	WdfWaitLockAcquire(Ctx->CoreLock, NULL);
	if (!Ctx->Attached || !Ctx->InD0)
		err = ENXIO;
	else
		err = iwx_tx_inject(Ctx->Sc, (const uint8_t *)h + h->HeaderSize,
		    h->FrameLength, &tp, NULL);
	WdfWaitLockRelease(Ctx->CoreLock);

	if (wantResult) {
		if (err == 0) {
			/* status may already be in (DPC ran meanwhile) */
			AirdropTxCompleteDone(Ctx);
			return;
		}
		WdfSpinLockAcquire(Ctx->TxLock);
		/* NULL: cancel routine already forgot (and completes) it */
		if (AqTxAbort(&Ctx->Tx, cookie) == NULL) {
			WdfSpinLockRelease(Ctx->TxLock);
			return;
		}
		WdfSpinLockRelease(Ctx->TxLock);
		if (WdfRequestUnmarkCancelable(Request) == STATUS_CANCELLED)
			return;
	}
	status = AirdropErrnoToStatus(err);
 complete:
	WdfRequestComplete(Request, status);
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
		AIRDROP_INFO full;
		PVOID out;
		size_t outLen;

		/* v1 callers pass the 144-byte v1 structure */
		status = WdfRequestRetrieveOutputBuffer(Request,
		    AIRDROP_INFO_V1_SIZE, &out, &outLen);
		if (!NT_SUCCESS(status))
			break;
		WdfWaitLockAcquire(ctx->CoreLock, NULL);
		AirdropFillInfo(ctx, &full);
		WdfWaitLockRelease(ctx->CoreLock);
		info = outLen < sizeof(full) ? AIRDROP_INFO_V1_SIZE :
		    sizeof(full);
		full.Size = (UINT32)info;
		RtlCopyMemory(out, &full, info);
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
		AirdropTxCompleteDone(ctx);	/* frames flushed by the stop */
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
		AirdropIoctlInject(ctx, Request, OutputBufferLength);
		return;		/* completes the request itself */

	default:
		break;
	}

	WdfRequestCompleteWithInformation(Request, status, info);
}

/* Last handle closed: fail its requests and switch the radio off. */
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

	/* flushed frames complete normally, anything left is cancelled */
	AirdropTxCompleteDone(ctx);
	AirdropTxCancelAll(ctx, FileObject, STATUS_CANCELLED);
}
