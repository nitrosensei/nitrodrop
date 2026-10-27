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
 * Radio (M2/M3). Fixed-channel IBSS-type MAC context + monitor RX +
 * raw injection. Currently stubs returning ENOTSUP, see iwx_radio.c.
 */
#define IWX_RADIO_F_LISTENER_ONLY	0x01	/* M2: no IBSS, pure monitor */
int	iwx_radio_start(struct iwx_softc *sc, uint8_t channel,
	    const uint8_t bssid[ETHER_ADDR_LEN], uint32_t flags);
int	iwx_radio_stop(struct iwx_softc *sc);

#define IWX_TX_F_NOACK		0x01
#define IWX_TX_F_RATE_HT	0x02	/* rate is an HT MCS index */
int	iwx_tx_inject(struct iwx_softc *sc, const uint8_t *frame,
	    uint32_t len, uint32_t rate, uint32_t flags);

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
	int		nchan;
	uint8_t		chan[64];	/* valid channel numbers */
	uint32_t	chan_flags[64];
};
void	iwx_get_info(struct iwx_softc *sc, struct iwx_info *info);

size_t	iwx_softc_size(void);

#endif /* AIRDROP_IWX_CORE_H */
