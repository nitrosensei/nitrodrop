/*	$OpenBSD: if_iwx.c,v 1.230 2026/09/19 19:36:42 stsp Exp $	*/

/*
 * Copyright (c) 2014, 2016 genua gmbh <info@genua.de>
 *   Author: Stefan Sperling <stsp@openbsd.org>
 * Copyright (c) 2014 Fixup Software Ltd.
 * Copyright (c) 2017, 2019, 2020 Stefan Sperling <stsp@openbsd.org>
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

/*-
 * Based on BSD-licensed source modules in the Linux iwlwifi driver,
 * which were used as the reference documentation for this implementation.
 *
 ******************************************************************************
 *
 * This file is provided under a dual BSD/GPLv2 license.  When using or
 * redistributing this file, you may do so under either license.
 *
 * GPL LICENSE SUMMARY
 *
 * Copyright(c) 2017 Intel Deutschland GmbH
 * Copyright(c) 2018 - 2019 Intel Corporation
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of version 2 of the GNU General Public License as
 * published by the Free Software Foundation.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * General Public License for more details.
 *
 * BSD LICENSE
 *
 * Copyright(c) 2017 Intel Deutschland GmbH
 * Copyright(c) 2018 - 2019 Intel Corporation
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 *  * Redistributions of source code must retain the above copyright
 *    notice, this list of conditions and the following disclaimer.
 *  * Redistributions in binary form must reproduce the above copyright
 *    notice, this list of conditions and the following disclaimer in
 *    the documentation and/or other materials provided with the
 *    distribution.
 *  * Neither the name Intel Corporation nor the names of its
 *    contributors may be used to endorse or promote products derived
 *    from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
 * A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
 * OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
 * LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 * DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
 * THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 *****************************************************************************
 */

/*-
 * Copyright (c) 2007-2010 Damien Bergamini <damien.bergamini@free.fr>
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
 * airdrop-win port notes (driver/core/iwx_radio.c)
 *
 * RX MPDU handling (from OpenBSD if_iwx.c rev 1.230 iwx_rx_mpdu_mq() and
 * iwx_rxmq_get_signal_strength()) plus the M2/M3 radio entry points,
 * which are stubs in milestone M1.
 *
 * Divergences in iwx_rx_mpdu_mq() (all intentional):
 *   D-RX1 no net80211: no hardware-decrypt check, duplicate detection,
 *         BA reordering or A-MSDU header fix-up. The raw 802.11 frame is
 *         copied to the OS backend together with its metadata
 *         (struct iwx_rx_meta). Frames are only delivered while the radio
 *         is started (sc_rx_enabled).
 *   D-RX2 the 2-byte firmware pad after the header (MFLG2_PAD) is removed
 *         for data/management frames the same way iwx does; header length
 *         is computed locally (no ieee80211_get_hdrlen()).
 *   D-RX3 minimum frame length is always the 10-byte ACK/CTS size (iwx
 *         uses it only in monitor mode); monitor-style capture wants
 *         control frames.
 */

#include "iwx_var.h"

/* local helper replacing ieee80211_get_hdrlen() (802.11-2020 9.2) */
static uint32_t
iwx_80211_hdrlen(const uint8_t *fc, uint32_t len)
{
	uint8_t type = fc[0] & IEEE80211_FC0_TYPE_MASK;
	uint8_t subtype = fc[0] & IEEE80211_FC0_SUBTYPE_MASK;
	uint32_t hdrlen;

	if (type == IEEE80211_FC0_TYPE_CTL) {
		switch (subtype) {
		case 0xc0:	/* CTS */
		case 0xd0:	/* ACK */
			return 10;
		default:
			return 16;	/* RTS, PS-Poll, BAR, ... */
		}
	}
	hdrlen = 24;
	if (type == IEEE80211_FC0_TYPE_DATA) {
		if ((fc[1] & 0x03) == 0x03)	/* ToDS + FromDS: addr4 */
			hdrlen += 6;
		if (subtype & 0x80)		/* QoS */
			hdrlen += 2;
	}
	if (fc[1] & 0x80 && type != IEEE80211_FC0_TYPE_CTL &&
	    (type == IEEE80211_FC0_TYPE_MGT || (subtype & 0x80)))
		hdrlen += 4;			/* +HTC (order bit) */
	return hdrlen > len ? len : hdrlen;
}

static int
iwx_rxmq_get_signal_strength(struct iwx_softc *sc,
    const struct iwx_rx_mpdu_desc *desc)
{
	int energy_a, energy_b;

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		energy_a = desc->v3.energy_a;
		energy_b = desc->v3.energy_b;
	} else {
		energy_a = desc->v1.energy_a;
		energy_b = desc->v1.energy_b;
	}
	energy_a = energy_a ? -energy_a : -256;
	energy_b = energy_b ? -energy_b : -256;
	return MAX(energy_a, energy_b);
}

/*
 * Called from iwx_rx_pkt() at DISPATCH_LEVEL with osdep_lock held.
 * 'pktdata' points at the struct iwx_rx_mpdu_desc inside the RB.
 */
void
iwx_rx_mpdu_mq(struct iwx_softc *sc, const void *pktdata, size_t maxlen)
{
	const struct iwx_rx_mpdu_desc *desc;
	const uint8_t *frame;
	struct iwx_rx_meta meta;
	uint32_t len, hdrlen;
	int rssi;
	size_t desc_size;
	/* worst-case 802.11 header (30 + QoS 2 + HTC 4) + 8-byte IV */
	uint8_t hdrbuf[48];
	/* frame bytes are modified in place for the pad removal below */

	if (!sc->sc_rx_enabled)
		return;

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
		desc_size = sizeof(*desc);
	else
		desc_size = IWX_RX_DESC_SIZE_V1;

	if (maxlen < desc_size) {
		sc->sc_stat_rx_dropped++;
		return; /* drop */
	}

	desc = (const struct iwx_rx_mpdu_desc *)pktdata;

	if (!(desc->status & htole32(IWX_RX_MPDU_RES_STATUS_CRC_OK)) ||
	    !(desc->status & htole32(IWX_RX_MPDU_RES_STATUS_OVERRUN_OK))) {
		sc->sc_stat_rx_dropped++;
		return; /* drop */
	}

	len = le16toh(desc->mpdu_len);

	/* D-RX3: allow control frames */
	if (len < IEEE80211_MIN_FRAME_LEN) {
		sc->sc_stat_rx_dropped++;
		return;
	}
	if (len > maxlen - desc_size) {
		sc->sc_stat_rx_dropped++;
		return;
	}

	frame = (const uint8_t *)pktdata + desc_size;

	memset(&meta, 0, sizeof(meta));
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		meta.rate_n_flags = le32toh(desc->v3.rate_n_flags);
		meta.channel = desc->v3.channel;
		meta.gp2 = le32toh(desc->v3.gp2_on_air_rise);
		meta.mac_context = desc->v3.mac_context;
	} else {
		meta.rate_n_flags = le32toh(desc->v1.rate_n_flags);
		meta.channel = desc->v1.channel;
		meta.gp2 = le32toh(desc->v1.gp2_on_air_rise);
		meta.mac_context = desc->v1.mac_context;
	}
	meta.phy_info = le16toh(desc->phy_info);
	meta.status = le32toh(desc->status);
	if (meta.phy_info & IWX_RX_MPDU_PHY_SHORT_PREAMBLE)
		meta.flags |= IWX_RXMETA_F_SHORTPRE;
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210 &&
	    !(meta.phy_info & IWX_RX_MPDU_PHY_TSF_OVERLOAD)) {
		meta.tsf = ((uint64_t)le32toh(desc->v3.tsf_on_air_rise1) << 32) |
		    le32toh(desc->v3.tsf_on_air_rise0);
		meta.flags |= IWX_RXMETA_F_TSF_VALID;
	}
	rssi = iwx_rxmq_get_signal_strength(sc, desc);
	if (rssi < -128)
		rssi = -128;
	meta.rssi_dbm = (int8_t)rssi;

	/* Account for padding following the frame header (D-RX2). */
	if (desc->mac_flags2 & IWX_RX_MPDU_MFLG2_PAD) {
		hdrlen = iwx_80211_hdrlen(frame, len);
		if ((le32toh(desc->status) &
		    IWX_RX_MPDU_RES_STATUS_SEC_ENC_MSK) ==
		    IWX_RX_MPDU_RES_STATUS_SEC_CCM_ENC) {
			/* Padding is inserted after the IV. */
			hdrlen += 8;	/* IEEE80211_CCMP_HDRLEN */
		}
		if (hdrlen + 2 <= len && hdrlen <= sizeof(hdrbuf)) {
			/*
			 * iwx: memmove(m_data + 2, m_data, hdrlen); m_adj(2).
			 * Same in place: the RB belongs to the driver until
			 * iwx_notif_intr() hands it back to the device.
			 */
			memcpy(hdrbuf, frame, hdrlen);
			memcpy((uint8_t *)(uintptr_t)frame + 2, hdrbuf, hdrlen);
			frame += 2;
			len -= 2;
		}
	}

	sc->sc_stat_rx_frames++;
	osdep_rx_frame(sc->sc_os, &meta, frame, len);
}

/* ---- M2 / M3 ------------------------------------------------------------ */

/*
 * iwx_radio_start() - TODO(M2/M3). Planned command sequence (all iwx
 * helpers exist in if_iwx.c and will be ported 1:1):
 *
 *  M2 (listen only, IWX_RADIO_F_LISTENER_ONLY):
 *   1. iwx_phy_ctxt_update(sc, &sc->sc_phyctxt[0], chan 'channel',
 *      IEEE80211_HTOP0_SCO_SCN, IEEE80211_VHTOP0_CHAN_WIDTH_HT, ACTION_ADD)
 *      -> PHY_CONTEXT_CMD (v3/v4 by iwx_lookup_cmd_ver).
 *   2. iwx_mac_ctxt_cmd(sc, in, IWX_FW_CTXT_ACTION_ADD, 0) with
 *      ic_opmode == IEEE80211_M_MONITOR: MAC_CONTEXT_CMD,
 *      mac_type FW_MAC_TYPE_LISTENER, filter_flags PROMISC |
 *      CONTROL | BEACON | PROBE_REQUEST | ACCEPT_GRP | IN_BEACON.
 *   3. iwx_binding_cmd(ACTION_ADD) and IWX_FLAG_BINDING_ACTIVE.
 *   4. iwx_add_sta_cmd() for IWX_MONITOR_STA_ID (STA_TYPE_GENERAL
 *      purpose, station_type IWX_STA_GENERAL_PURPOSE) +
 *      iwx_enable_txq() of the default data queue (alloc txq[1], D-TR4).
 *   5. sc_rx_enabled = 1 -> MPDUs reach osdep_rx_frame().
 *
 *  M3 (IBSS-type context on the fixed channel, BSSID from 'bssid'):
 *   - MAC_CONTEXT_CMD with FW_MAC_TYPE_IBSS, bssid_addr = bssid,
 *     beacon template (BEACON_TEMPLATE_CMD v10+) only if beaconing is
 *     really required by AWDL-style peers (to be decided, see
 *     docs/driver-research.md), TIME_QUOTA / session protection so the
 *     firmware stays on 'channel'.
 *   - iwx_tx_inject(): TX_CMD (gen3: struct iwx_tx_cmd_gen3) on the
 *     data queue, TX_CMD_FLG_ACK cleared for NOACK, rate_n_flags from
 *     'rate' (legacy OFDM index or HT MCS, rate_n_flags v2 format when
 *     sc_rate_n_flags_version == 2), sta_id = IWX_MONITOR_STA_ID,
 *     completion through IWX_TX_CMD notification (iwx_rx_tx_cmd).
 */
int
iwx_radio_start(struct iwx_softc *sc, uint8_t channel,
    const uint8_t bssid[ETHER_ADDR_LEN], uint32_t flags)
{
	int i, valid = 0;

	(void)bssid;
	(void)flags;

	if (sc->sc_state != IWX_STATE_FW_READY)
		return ENXIO;

	for (i = 0; i < sc->sc_nvm.nchan; i++) {
		if (sc->sc_nvm.channels[i].ieee == channel)
			valid = 1;
	}
	if (!valid)
		return EINVAL;

	/* TODO(M2): PHY context, MAC context (listener), binding, STA. */
	return ENOTSUP;
}

int
iwx_radio_stop(struct iwx_softc *sc)
{
	sc->sc_rx_enabled = 0;
	if (sc->sc_state == IWX_STATE_RADIO_ON)
		sc->sc_state = IWX_STATE_FW_READY;
	/* TODO(M2): remove STA, binding, MAC and PHY contexts (iwx order). */
	return 0;
}

int
iwx_tx_inject(struct iwx_softc *sc, const uint8_t *frame, uint32_t len,
    uint32_t rate, uint32_t flags)
{
	(void)frame;
	(void)rate;
	(void)flags;

	if (sc->sc_state != IWX_STATE_RADIO_ON)
		return ENXIO;
	if (len < IEEE80211_MIN_FRAME_LEN || len > 2304 + 36)
		return EINVAL;
	/* TODO(M3): TX_CMD gen3 on the data queue, see iwx_radio_start(). */
	return ENOTSUP;
}
