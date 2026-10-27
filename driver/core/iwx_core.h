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
 * Public interface of the portable iwx core, used by the OS backend.
 *
 * Threading contract (the backend enforces it):
 *   - every function below except the iwx_isr_* / iwx_dpc_* pair is
 *     called at PASSIVE_LEVEL and serialized by one device mutex
 *     (the role of OpenBSD's ioctl_rwl);
 *   - iwx_isr_* run at DIRQL and only touch MMIO;
 *   - iwx_dpc_* run at DISPATCH_LEVEL, never concurrently with each other;
 *     they share ring state with iwx_send_cmd() under osdep_lock().
 * All functions return 0 or an errno value.
 */
#ifndef AIRDROP_IWX_CORE_H
#define AIRDROP_IWX_CORE_H

#include "osdep.h"

struct iwx_softc;

/* Lifetime: attach after BAR0 is mapped; detach before it is unmapped. */
int	iwx_attach(struct iwx_softc *sc, void *os, uint16_t pci_device,
	    uint16_t pci_subdevice);
void	iwx_detach(struct iwx_softc *sc);

/* MSI-X (1) or MSI/INTx (0); set before iwx_preinit(). */
void	iwx_set_msix(struct iwx_softc *sc, int msix);

/*
 * iwx_preinit(): first boot of the firmware to read the NVM (MAC address,
 * channel list), then stop the device again - exactly OpenBSD's
 * iwx_preinit(). Later calls only re-run iwx_prepare_card_hw().
 */
int	iwx_preinit(struct iwx_softc *sc);

/* iwx_init(): start_hw + firmware + iwx_init_hw(). State -> FW_READY. */
int	iwx_init(struct iwx_softc *sc);

/* iwx_stop(): radio off, device stopped and in low power. State -> DOWN. */
void	iwx_stop(struct iwx_softc *sc);

/* Interrupts, MSI-X: ISR reads + acks causes, DPC processes them. */
void	iwx_isr_msix(struct iwx_softc *sc, uint32_t *inta_fh, uint32_t *inta_hw);
void	iwx_dpc_msix(struct iwx_softc *sc, uint32_t inta_fh, uint32_t inta_hw);
/* Interrupts, MSI/INTx: ISR masks (returns 0 if not ours), DPC handles. */
int	iwx_isr_legacy(struct iwx_softc *sc);
void	iwx_dpc_legacy(struct iwx_softc *sc);

/* Dump LMAC/UMAC error tables to the log (PASSIVE, after a fatal error). */
void	iwx_dump_error_log(struct iwx_softc *sc);

/*
 * Radio (M2/M3), see iwx_radio.c for the exact firmware command sequences.
 *
 * M2 (IWX_RADIO_F_LISTENER_ONLY): LISTENER MAC context with promiscuous
 *   filters on a fixed 20 MHz channel, exactly iwx's monitor mode
 *   (iwx_auth() + iwx_run() for IEEE80211_M_MONITOR). Every frame the
 *   firmware passes up is delivered through osdep_rx_frame(). Injection
 *   uses the monitor station's queue.
 * M3 (default): IBSS MAC context with our MAC address and 'bssid' (Owly:
 *   00:25:00:ff:94:73) so the firmware ACKs unicast frames addressed to
 *   us; promiscuous RX is kept (unless IWX_RADIO_F_NO_PROMISC); multicast
 *   and broadcast stations + queues like Linux iwlmvm's IBSS start.
 */
#define IWX_RADIO_F_LISTENER_ONLY	0x01	/* M2: no IBSS, pure monitor */
#define IWX_RADIO_F_KEEP_BAD_FCS	0x02	/* deliver CRC-failed frames */
#define IWX_RADIO_F_NO_PROMISC		0x04	/* M3: IBSS filters only */

#define IWX_RADIO_DEFAULT_BINT		10000	/* TU, Owly IBSS_BEACON_TU */

struct iwx_radio_params {
	uint8_t		channel;		/* IEEE channel number */
	uint8_t		bssid[ETHER_ADDR_LEN];	/* M3 IBSS BSSID */
	uint8_t		reserved;
	uint16_t	beacon_int;		/* M3, TU; 0 = default */
	uint16_t	reserved2;
	uint32_t	flags;			/* IWX_RADIO_F_* */
};
/* EBUSY if already on, ENXIO if firmware is not ready, EINVAL on params */
int	iwx_radio_start(struct iwx_softc *sc, const struct iwx_radio_params *p);
/* Idempotent; falls back to a full device stop if teardown fails. */
int	iwx_radio_stop(struct iwx_softc *sc);

#define IWX_TX_F_NOACK		0x01	/* see iwx_tx_inject() */
#define IWX_TX_F_RATE_HT	0x02	/* rate is an HT MCS index 0..15 */
#define IWX_TX_F_ASSIGN_SEQ	0x04	/* driver writes the sequence number */
#define IWX_TX_F_SGI		0x08	/* HT short guard interval */
#define IWX_TX_F_HIGH_PRI	0x10	/* TX_FLAGS_HIGH_PRI */

struct iwx_tx_params {
	uint32_t	rate;		/* legacy: 500 kb/s units; HT: MCS */
	uint32_t	flags;		/* IWX_TX_F_* */
	uint32_t	cookie;		/* echoed in osdep_tx_status() */
};
/* Frames in flight per TX queue (payload DMA slots). */
#define IWX_TX_MAX_INFLIGHT	64
#define IWX_TX_MAX_FRAME	2346	/* 802.11 MPDU without FCS */
/*
 * Queue one raw 802.11 frame (no FCS). Returns 0 and *txr_flags
 * (IWX_TXR_F_*) when the frame was handed to the device; exactly one
 * osdep_tx_status() follows. ENXIO: radio off; EINVAL: bad frame/rate;
 * ENOBUFS: IWX_TX_MAX_INFLIGHT frames pending.
 */
int	iwx_tx_inject(struct iwx_softc *sc, const uint8_t *frame,
	    uint32_t len, const struct iwx_tx_params *p, uint32_t *txr_flags);

/* Read-only accessors for the backend. */
struct iwx_info {
	uint8_t		macaddr[ETHER_ADDR_LEN];
	char		fwver[32];
	uint32_t	hw_rev;
	uint32_t	hw_rf_id;
	int		state;		/* enum iwx_radio_state */
	int		rfkill;
	int		msix;
	uint32_t	last_fw_error;
	uint32_t	rx_frames;
	uint32_t	rx_dropped;
	uint32_t	rx_bad_fcs;	/* delivered or dropped CRC failures */
	uint32_t	tx_submitted;
	uint32_t	tx_success;
	uint32_t	tx_failed;
	uint32_t	tx_flushed;
	uint32_t	tx_pending;
	uint8_t		radio_channel;	/* 0 when radio off */
	uint8_t		radio_mode;	/* 0 off, 1 listener (M2), 2 IBSS (M3) */
	int		nchan;
	uint8_t		chan[64];	/* valid channel numbers */
	uint32_t	chan_flags[64];
};
void	iwx_get_info(struct iwx_softc *sc, struct iwx_info *info);

size_t	iwx_softc_size(void);

#endif /* AIRDROP_IWX_CORE_H */
