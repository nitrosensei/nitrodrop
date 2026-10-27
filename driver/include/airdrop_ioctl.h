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
 *   GET_INFO      out: AIRDROP_INFO (a v1 caller may pass the 144-byte
 *                      v1 size and gets the v1 prefix)
 *   START         in : AIRDROP_START_PARAMS     (firmware up + radio on;
 *                      the 16-byte v1 layout is accepted, BeaconIntervalTu
 *                      then defaults to 10000 TU)
 *   STOP          -                            (radio off, device stopped)
 *   READ_FRAME    out: AIRDROP_RX_HEADER + frame bytes; pends until a
 *                      frame is available - use OVERLAPPED and keep
 *                      several requests outstanding. The driver keeps a
 *                      bounded ring (128 frames); when it is full new
 *                      frames are dropped and counted (DroppedSinceLast,
 *                      AIRDROP_INFO.RxDropped).
 *   INJECT_FRAME  in : AIRDROP_TX_HEADER + frame bytes (raw 802.11 MPDU,
 *                      no FCS; the hardware appends it).
 *                 out: optional AIRDROP_TX_RESULT. With an output buffer
 *                      the request pends until the firmware reports the
 *                      transmission (ACK / retries); without one it
 *                      completes as soon as the frame is queued.
 *                      STATUS_DEVICE_BUSY: 64 frames already in flight.
 *
 * Status (ABI v2): START with AIRDROP_START_F_LISTENER_ONLY = M2 monitor
 * (LISTENER MAC context, promiscuous, fixed 20 MHz channel); without it =
 * M3 IBSS context (Owly-style AWDL: our MAC, Bssid as given, firmware ACKs
 * unicast frames addressed to us) that keeps promiscuous reception unless
 * AIRDROP_START_F_NO_PROMISC. Injection works in both modes. Only tested
 * against the device model (driver/tests), not yet on hardware.
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

#define AIRDROP_ABI_VERSION		2

#define FILE_DEVICE_AIRDROP		0x8a57u	/* custom range >= 0x8000 (unsigned: CTL_CODE fits DWORD) */

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
	/* ---- ABI v2 (offset 144) ---- */
	UINT32	RxBadFcs;		/* CRC/overrun failures seen */
	UINT32	TxSubmitted;		/* frames queued to the firmware */
	UINT32	TxSuccess;		/* ACKed (or no ACK expected) */
	UINT32	TxFailed;		/* retry limit etc. */
	UINT32	TxFlushed;		/* dropped by flush / stop / reset */
	UINT32	TxPending;		/* currently in flight */
	UINT8	RadioChannel;		/* 0 when the radio is off */
	UINT8	RadioMode;		/* AIRDROP_RADIO_MODE_* */
	UINT16	Reserved1;
	UINT32	Reserved2;
} AIRDROP_INFO, *PAIRDROP_INFO;

#define AIRDROP_INFO_V1_SIZE		144

#define AIRDROP_RADIO_MODE_OFF		0
#define AIRDROP_RADIO_MODE_LISTENER	1	/* M2 */
#define AIRDROP_RADIO_MODE_IBSS		2	/* M3 */

#define AIRDROP_START_F_LISTENER_ONLY	0x00000001	/* M2: monitor, no IBSS */
#define AIRDROP_START_F_KEEP_BAD_FCS	0x00000002	/* deliver CRC failures */
#define AIRDROP_START_F_NO_PROMISC	0x00000004	/* IBSS: own BSS only */

typedef struct _AIRDROP_START_PARAMS {
	UINT32	Size;			/* sizeof(AIRDROP_START_PARAMS) */
	UINT32	Flags;			/* AIRDROP_START_F_* */
	UINT8	Channel;		/* IEEE channel, e.g. 6 */
	UINT8	Bssid[6];		/* IBSS: e.g. 00:25:00:ff:94:73 */
	UINT8	Reserved;
	/* ---- ABI v2 (offset 16) ---- */
	UINT16	BeaconIntervalTu;	/* IBSS beacon interval, 0 = 10000 */
	UINT16	Reserved1;
	UINT32	Reserved2;
} AIRDROP_START_PARAMS, *PAIRDROP_START_PARAMS;

#define AIRDROP_START_PARAMS_V1_SIZE	16

#define AIRDROP_RX_F_SHORT_PREAMBLE	0x0001	/* CCK only */
#define AIRDROP_RX_F_TSF_VALID		0x0002
#define AIRDROP_RX_F_TRUNCATED		0x0004	/* frame cut to buffer/slot */
#define AIRDROP_RX_F_FCS_STRIPPED	0x0008	/* always: no FCS in frame */
#define AIRDROP_RX_F_BAD_FCS		0x0010	/* CRC failed (KEEP_BAD_FCS) */
#define AIRDROP_RX_F_AMPDU		0x0020	/* AmpduRef valid */
#define AIRDROP_RX_F_SGI		0x0040
#define AIRDROP_RX_F_LDPC		0x0080
#define AIRDROP_RX_F_STBC		0x0100
#define AIRDROP_RX_F_AMSDU		0x0200

#define AIRDROP_RX_ENC_CCK		0
#define AIRDROP_RX_ENC_OFDM		1
#define AIRDROP_RX_ENC_HT		2
#define AIRDROP_RX_ENC_VHT		3
#define AIRDROP_RX_ENC_HE		4
#define AIRDROP_RX_ENC_EHT		5

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
	UINT16	PhyInfo;		/* firmware phy_info (raw), v1: Reserved */
	UINT32	DroppedSinceLast;	/* frames lost before this one */
	/* ---- ABI v2 (offset 48) ---- */
	UINT32	AmpduRef;		/* same value = same A-MPDU (AMPDU flag) */
	UINT16	LegacyRate;		/* CCK/OFDM: 500 kb/s units, else 0 */
	UINT8	Mcs;			/* HT/VHT/HE MCS, 0xff for legacy */
	UINT8	Nss;			/* spatial streams */
	UINT8	Bandwidth;		/* 0 = 20, 1 = 40, 2 = 80, 3 = 160 MHz */
	UINT8	Encoding;		/* AIRDROP_RX_ENC_* */
	UINT8	MacContext;		/* firmware MAC context id */
	UINT8	Reserved2;
	UINT32	MpduStatus;		/* firmware RX status word (raw) */
} AIRDROP_RX_HEADER, *PAIRDROP_RX_HEADER;

/* Recommended READ_FRAME buffer: header + largest RX buffer (4 KiB). */
#define AIRDROP_RX_BUFFER_SIZE		(sizeof(AIRDROP_RX_HEADER) + 4096)

/*
 * AIRDROP_TX_F_NOACK: the TX_CMD API of this firmware has no "no ACK"
 * bit. For unicast QoS data the driver sets the QoS Ack Policy to No Ack;
 * other unicast frames are sent normally and the result carries
 * AIRDROP_TXR_F_NOACK_NOT_HONORED. Group-addressed frames are never ACKed.
 */
#define AIRDROP_TX_F_NOACK		0x00000001
#define AIRDROP_TX_F_RATE_HT		0x00000002	/* Rate is HT MCS 0-15 */
#define AIRDROP_TX_F_ASSIGN_SEQ		0x00000004	/* driver sequence number */
#define AIRDROP_TX_F_SGI		0x00000008	/* HT short guard interval */
#define AIRDROP_TX_F_HIGH_PRI		0x00000010	/* firmware high priority */

#define AIRDROP_TX_MAX_FRAME		2346

typedef struct _AIRDROP_TX_HEADER {
	UINT32	HeaderSize;		/* sizeof(AIRDROP_TX_HEADER); frame follows */
	UINT32	FrameLength;		/* 802.11 MPDU length, no FCS */
	UINT32	Rate;			/* legacy: rate in 500 kb/s units (12 = 6M)
					 * HT: MCS index (with AIRDROP_TX_F_RATE_HT) */
	UINT32	Flags;			/* AIRDROP_TX_F_* */
} AIRDROP_TX_HEADER, *PAIRDROP_TX_HEADER;

#define AIRDROP_TXST_SUCCESS		0	/* ACKed, or no ACK expected */
#define AIRDROP_TXST_FAILED		1	/* retries exhausted etc. */
#define AIRDROP_TXST_FLUSHED		2	/* dropped by stop/flush/reset */
#define AIRDROP_TXST_NO_STATUS		3	/* sent, firmware gave no result */

#define AIRDROP_TXR_F_NOACK_NOT_HONORED	0x0001
#define AIRDROP_TXR_F_ACK_POLICY_SET	0x0002	/* QoS Ack Policy rewritten */
#define AIRDROP_TXR_F_SEQ_ASSIGNED	0x0004

typedef struct _AIRDROP_TX_RESULT {
	UINT32	Size;			/* sizeof(AIRDROP_TX_RESULT) */
	UINT32	Status;			/* AIRDROP_TXST_* */
	UINT32	FwStatus;		/* firmware TX status (raw, 0 if none) */
	UINT8	Retries;		/* data frame retries */
	UINT8	RtsRetries;
	UINT16	Sequence;		/* sequence control field sent */
	UINT32	InitialRate;		/* firmware rate_n_flags used */
	UINT32	Flags;			/* AIRDROP_TXR_F_* */
} AIRDROP_TX_RESULT, *PAIRDROP_TX_RESULT;

#if defined(__cplusplus)
static_assert(sizeof(AIRDROP_INFO) == 176, "AIRDROP_INFO layout");
static_assert(sizeof(AIRDROP_START_PARAMS) == 24, "AIRDROP_START_PARAMS layout");
static_assert(sizeof(AIRDROP_RX_HEADER) == 64, "AIRDROP_RX_HEADER layout");
static_assert(sizeof(AIRDROP_TX_HEADER) == 16, "AIRDROP_TX_HEADER layout");
static_assert(sizeof(AIRDROP_TX_RESULT) == 24, "AIRDROP_TX_RESULT layout");
#else
typedef char airdrop_assert_info[(sizeof(AIRDROP_INFO) == 176) ? 1 : -1];
typedef char airdrop_assert_start[(sizeof(AIRDROP_START_PARAMS) == 24) ? 1 : -1];
typedef char airdrop_assert_rx[(sizeof(AIRDROP_RX_HEADER) == 64) ? 1 : -1];
typedef char airdrop_assert_tx[(sizeof(AIRDROP_TX_HEADER) == 16) ? 1 : -1];
typedef char airdrop_assert_txr[(sizeof(AIRDROP_TX_RESULT) == 24) ? 1 : -1];
#endif

#endif /* AIRDROP_IOCTL_H */
