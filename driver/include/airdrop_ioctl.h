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
 * airdrop-win Wi-Fi driver - user/kernel IOCTL ABI.
 *
 * Usable from kernel mode (driver) and user mode (C, C++, or as reference
 * for Rust/C# bindings). All structures use natural alignment with
 * fixed-size fields, identical on x64 and ARM64; every structure starts
 * with a size or version field so it can grow.
 *
 * Opening the device:
 *   SetupDiGetClassDevs / CM_Get_Device_Interface_List with
 *   GUID_DEVINTERFACE_AIRDROP_WIFI, then CreateFile(path,
 *   GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
 *   FILE_FLAG_OVERLAPPED, NULL). The device is exclusive (one handle) and
 *   its default ACL admits SYSTEM and Administrators only (INF).
 *   Closing the last handle stops the radio.
 *
 * IOCTLs:
 *   GET_INFO      out: AIRDROP_INFO
 *   START         in : AIRDROP_START_PARAMS     (firmware up + radio on)
 *   STOP          -                            (radio off, device stopped)
 *   READ_FRAME    out: AIRDROP_RX_HEADER + frame bytes; pends until a
 *                      frame is available - use OVERLAPPED and keep
 *                      several requests outstanding.
 *   INJECT_FRAME  in : AIRDROP_TX_HEADER + frame bytes (raw 802.11 MPDU,
 *                      no FCS; the hardware appends it).
 *
 * Milestone M1 status: GET_INFO and STOP are complete. START brings the
 * firmware up (STATE_FW_READY) and then fails with
 * STATUS_NOT_IMPLEMENTED until the M2 MAC/PHY context code lands.
 * INJECT_FRAME returns STATUS_NOT_IMPLEMENTED (M3).
 */
#ifndef AIRDROP_IOCTL_H
#define AIRDROP_IOCTL_H

#if defined(_KERNEL_MODE) || defined(_NTDDK_) || defined(_WDMDDK_)
#include <ntddk.h>
#else
#include <windows.h>
#include <winioctl.h>
#endif

/*
 * The GUID is only declared here. Exactly one translation unit must
 * include <initguid.h> before this header to instantiate it.
 */
/* {6c1b3e0a-9d42-4f5e-8a71-2f3c4b5d6e7f} */
DEFINE_GUID(GUID_DEVINTERFACE_AIRDROP_WIFI,
    0x6c1b3e0a, 0x9d42, 0x4f5e, 0x8a, 0x71, 0x2f, 0x3c, 0x4b, 0x5d, 0x6e, 0x7f);

#define AIRDROP_ABI_VERSION		1

#define FILE_DEVICE_AIRDROP		0x8a57	/* custom range >= 0x8000 */

#define IOCTL_AIRDROP_GET_INFO \
    CTL_CODE(FILE_DEVICE_AIRDROP, 0x800, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_AIRDROP_START \
    CTL_CODE(FILE_DEVICE_AIRDROP, 0x801, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_AIRDROP_STOP \
    CTL_CODE(FILE_DEVICE_AIRDROP, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_AIRDROP_READ_FRAME \
    CTL_CODE(FILE_DEVICE_AIRDROP, 0x803, METHOD_OUT_DIRECT, FILE_READ_ACCESS)
#define IOCTL_AIRDROP_INJECT_FRAME \
    CTL_CODE(FILE_DEVICE_AIRDROP, 0x804, METHOD_BUFFERED, FILE_WRITE_ACCESS)

/* Driver / radio state (AIRDROP_INFO.state). */
#define AIRDROP_STATE_DOWN		0	/* device idle, firmware stopped */
#define AIRDROP_STATE_FW_READY		1	/* firmware running, radio not set up */
#define AIRDROP_STATE_RADIO_ON		2	/* fixed channel, RX/TX active */
#define AIRDROP_STATE_ERROR		3	/* fatal error; STOP then START */

#define AIRDROP_MAX_CHANNELS		64

typedef struct _AIRDROP_INFO {
	UINT32	Size;			/* sizeof(AIRDROP_INFO), set by driver */
	UINT32	AbiVersion;		/* AIRDROP_ABI_VERSION */
	UINT32	State;			/* AIRDROP_STATE_* */
	INT32	InitStatus;		/* NTSTATUS of the boot-time NVM read */
	UINT8	MacAddress[6];		/* permanent address from NVM/OTP */
	UINT8	RfKill;			/* 1 = hardware radio switch off */
	UINT8	Msix;			/* 1 = MSI-X, 0 = MSI/INTx */
	CHAR	FirmwareVersion[32];	/* e.g. "77.f92b5fed.0", NUL-terminated */
	UINT32	HwRev;			/* CSR_HW_REV (iwx format) */
	UINT32	HwRfId;			/* CSR_HW_RF_ID */
	UINT32	LastFirmwareError;	/* error_id of the last firmware assert */
	UINT32	RxFrames;		/* frames delivered by the firmware */
	UINT32	RxDropped;		/* frames dropped (driver ring full etc.) */
	UINT32	ChannelCount;		/* entries valid in Channels[] */
	UINT8	Channels[AIRDROP_MAX_CHANNELS];	/* usable channel numbers */
} AIRDROP_INFO, *PAIRDROP_INFO;

#define AIRDROP_START_F_LISTENER_ONLY	0x00000001	/* M2: monitor, no IBSS */

typedef struct _AIRDROP_START_PARAMS {
	UINT32	Size;			/* sizeof(AIRDROP_START_PARAMS) */
	UINT32	Flags;			/* AIRDROP_START_F_* */
	UINT8	Channel;		/* IEEE channel, e.g. 6 */
	UINT8	Bssid[6];		/* e.g. 00:25:00:ff:94:73 */
	UINT8	Reserved;
} AIRDROP_START_PARAMS, *PAIRDROP_START_PARAMS;

#define AIRDROP_RX_F_SHORT_PREAMBLE	0x0001
#define AIRDROP_RX_F_TSF_VALID		0x0002
#define AIRDROP_RX_F_TRUNCATED		0x0004	/* user buffer too small */

typedef struct _AIRDROP_RX_HEADER {
	UINT32	HeaderSize;		/* sizeof(AIRDROP_RX_HEADER); frame follows */
	UINT32	FrameLength;		/* bytes of 802.11 frame copied after header */
	UINT32	OriginalLength;		/* MPDU length as received (no FCS) */
	UINT32	Flags;			/* AIRDROP_RX_F_* */
	UINT64	HostTimestamp;		/* KeQueryInterruptTimePrecise, 100 ns */
	UINT64	Tsf;			/* TSF at start of frame (if TSF_VALID) */
	UINT32	DeviceTimestampUs;	/* firmware GP2 at start of frame */
	UINT32	RateNFlags;		/* firmware rate_n_flags (raw, see iwx_reg.h) */
	INT8	RssiDbm;		/* max over chains, dBm */
	UINT8	Channel;		/* channel the frame was received on */
	UINT16	Reserved;
	UINT32	DroppedSinceLast;	/* frames lost before this one */
} AIRDROP_RX_HEADER, *PAIRDROP_RX_HEADER;

/* Recommended READ_FRAME buffer: header + largest RX buffer (4 KiB). */
#define AIRDROP_RX_BUFFER_SIZE		(sizeof(AIRDROP_RX_HEADER) + 4096)

#define AIRDROP_TX_F_NOACK		0x00000001	/* do not expect an ACK */
#define AIRDROP_TX_F_RATE_HT		0x00000002	/* Rate is HT MCS index */

#define AIRDROP_TX_MAX_FRAME		2346

typedef struct _AIRDROP_TX_HEADER {
	UINT32	HeaderSize;		/* sizeof(AIRDROP_TX_HEADER); frame follows */
	UINT32	FrameLength;		/* 802.11 MPDU length, no FCS */
	UINT32	Rate;			/* legacy: rate in 500 kb/s units (12 = 6M)
					 * HT: MCS index (with AIRDROP_TX_F_RATE_HT) */
	UINT32	Flags;			/* AIRDROP_TX_F_* */
} AIRDROP_TX_HEADER, *PAIRDROP_TX_HEADER;

#if defined(__cplusplus)
static_assert(sizeof(AIRDROP_INFO) == 144, "AIRDROP_INFO layout");
static_assert(sizeof(AIRDROP_START_PARAMS) == 16, "AIRDROP_START_PARAMS layout");
static_assert(sizeof(AIRDROP_RX_HEADER) == 48, "AIRDROP_RX_HEADER layout");
static_assert(sizeof(AIRDROP_TX_HEADER) == 16, "AIRDROP_TX_HEADER layout");
#else
typedef char airdrop_assert_info[(sizeof(AIRDROP_INFO) == 144) ? 1 : -1];
typedef char airdrop_assert_start[(sizeof(AIRDROP_START_PARAMS) == 16) ? 1 : -1];
typedef char airdrop_assert_rx[(sizeof(AIRDROP_RX_HEADER) == 48) ? 1 : -1];
typedef char airdrop_assert_tx[(sizeof(AIRDROP_TX_HEADER) == 16) ? 1 : -1];
#endif

#endif /* AIRDROP_IOCTL_H */
