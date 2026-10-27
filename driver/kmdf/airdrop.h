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
 * KMDF backend for the portable iwx core (driver/core).
 *
 * Locking (see also core/iwx_core.h):
 *   CoreLock  (WDFWAITLOCK, PASSIVE): serializes every iwx_* call except
 *             ISR/DPC: PnP/power callbacks, IOCTLs, fatal work item.
 *   CoreSpin  (WDFSPINLOCK): osdep_lock(); ring state shared by
 *             iwx_send_cmd() and the DPC.
 *   RxLock    (WDFSPINLOCK): RX frame ring + READ_FRAME queue. Taken while
 *             CoreSpin is held (DPC -> osdep_rx_frame), never the reverse.
 *   TxLock    (WDFSPINLOCK): TX result slots (pending INJECT_FRAME
 *             requests). Taken while CoreSpin is held (osdep_tx_status),
 *             never the reverse. Requests are only completed with no
 *             spinlock held (AirdropTxCompleteDone).
 */
#ifndef AIRDROP_KMDF_H
#define AIRDROP_KMDF_H

#include <ntddk.h>
#include <wdf.h>
#include <wdmguid.h>

#include "../include/airdrop_ioctl.h"
#include "../core/iwx_core.h"
#include "airdrop_queues.h"	/* RX ring / TX slot bookkeeping */

#ifndef PCI_WHICH_SPACE_CONFIG
#define PCI_WHICH_SPACE_CONFIG		0x0	/* BUS_INTERFACE_STANDARD */
#endif

#define AIRDROP_POOL_TAG		'wxdA'	/* "Adxw" in pool dumps */
#define AIRDROP_FW_MAX_SIZE		(8u * 1024 * 1024)
#define AIRDROP_RX_RING_SLOTS		AQ_RX_SLOTS	/* 128 */
#define AIRDROP_RX_SLOT_DATA		4096

typedef struct _AIRDROP_RX_SLOT {
	AIRDROP_RX_HEADER	Hdr;
	UINT8			Data[AIRDROP_RX_SLOT_DATA];
} AIRDROP_RX_SLOT;

typedef struct _DEVICE_CONTEXT {
	WDFDEVICE		Device;

	/* Hardware resources */
	PUCHAR			Bar0;
	SIZE_T			Bar0Length;
	BUS_INTERFACE_STANDARD	Bus;
	BOOLEAN			BusValid;
	USHORT			PciDevice;
	USHORT			PciSubdevice;
	WDFDMAENABLER		DmaEnabler;
	WDFINTERRUPT		Interrupt;
	BOOLEAN			MessageSignaled;
	BOOLEAN			Msix;

	/* Core */
	struct iwx_softc	*Sc;
	BOOLEAN			Attached;
	BOOLEAN			InD0;
	NTSTATUS		InitStatus;
	WDFWAITLOCK		CoreLock;
	WDFSPINLOCK		CoreSpin;
	KEVENT			Events[OSDEP_EV_COUNT];

	/* Interrupt causes handed from ISR to DPC (MSI-X) */
	volatile LONG		PendingFh;
	volatile LONG		PendingHw;

	/* Fatal error handling (DPC -> PASSIVE) */
	WDFWORKITEM		FatalWork;
	volatile LONG		FatalReasons;	/* bit (1 << reason) */

	/* RX path */
	WDFQUEUE		ReadQueue;	/* manual: pending READ_FRAME */
	WDFSPINLOCK		RxLock;
	AIRDROP_RX_SLOT		*RxRing;	/* AIRDROP_RX_RING_SLOTS entries */
	AQ_RX_RING		Rx;		/* indices + drop counters */

	/* TX results (INJECT_FRAME with an AIRDROP_TX_RESULT buffer) */
	WDFSPINLOCK		TxLock;
	AQ_TX_TABLE		Tx;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, AirdropGetContext)

/* driver.c */
EVT_WDF_DRIVER_DEVICE_ADD		AirdropEvtDeviceAdd;
NTSTATUS AirdropErrnoToStatus(int err);
VOID AirdropQueueFatal(_In_ PDEVICE_CONTEXT Ctx, _In_ int Reason);

/* ioctl.c */
NTSTATUS AirdropQueueInitialize(_In_ PDEVICE_CONTEXT Ctx);
EVT_WDF_FILE_CLEANUP			AirdropEvtFileCleanup;
VOID AirdropRxDeliverPending(_In_ PDEVICE_CONTEXT Ctx);
VOID AirdropRxFlush(_In_ PDEVICE_CONTEXT Ctx, _In_ NTSTATUS Status);
VOID AirdropRadioDownLocked(_In_ PDEVICE_CONTEXT Ctx);
VOID AirdropTxCompleteDone(_In_ PDEVICE_CONTEXT Ctx);
VOID AirdropTxCancelAll(_In_ PDEVICE_CONTEXT Ctx,
    _In_opt_ WDFFILEOBJECT FileObject, _In_ NTSTATUS Status);

#endif /* AIRDROP_KMDF_H */
