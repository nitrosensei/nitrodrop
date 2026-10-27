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
 * RX MPDU handling (OpenBSD if_iwx.c rev 1.230 iwx_rx_mpdu_mq(),
 * iwx_rxmq_get_signal_strength()), the M2 monitor bring-up (iwx_auth() +
 * iwx_run() for IEEE80211_M_MONITOR and the reverse iwx_run_stop() +
 * iwx_deauth()), the M3 IBSS context (Linux iwlwifi mvm, used under its
 * BSD-3-Clause license, notice above) and raw injection (iwx_tx(),
 * iwx_tx_update_byte_tbl(), iwx_rx_tx_cmd(), iwx_txq_advance()).
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
 *   D-RX4 FCS handling follows Linux iwl_mvm_create_skb(): with the
 *         IN_CRC32 filter (monitor, RX_INCLUDES_FCS) the 4-byte FCS is
 *         removed (mac80211 does it there); without it the
 *         MIC_CRC_LEN bytes of mac_flags1 are removed. Frames never
 *         carry an FCS upward; IWX_RXMETA_F_FCS_STRIPPED says so.
 *   D-RX5 CRC/overrun failures are dropped like iwx unless the radio was
 *         started with IWX_RADIO_F_KEEP_BAD_FCS (Linux keeps them in
 *         monitor mode with RX_FLAG_FAILED_FCS_CRC).
 *   D-RX6 rate_n_flags is decoded (v1 and v2 layout) into encoding, MCS,
 *         NSS, bandwidth, SGI/LDPC/STBC; A-MPDU reference as in Linux
 *         (toggle bit, never 0). Short preamble is reported for CCK
 *         only (bit 7 of phy_info means NCCK_ADDTL_NTFY for OFDM).
 *
 * Radio bring-up / teardown (D-RA*):
 *   D-RA1 iwx keys everything off net80211 state (ic_opmode, ic_bss); the
 *         port passes the same values explicitly. For M2 they are the
 *         values a monitor-mode ic_bss has in OpenBSD (node allocated
 *         zeroed by ieee80211_node_lateattach(), never set up): intval 0,
 *         dtimperiod 0, associd 0, macaddr 00:00:00:00:00:00, no HT flag.
 *   D-RA2 iwx_flush_sta()/iwx_drain_sta()/iwx_disable_mgmt_queue() use
 *         IWX_STATION_ID (0) unconditionally; in monitor mode the only
 *         station is IWX_MONITOR_STA_ID (2). The port uses the station
 *         that was actually added (latent iwx bug, harmless there because
 *         iwx never flushes a monitor station with frames queued).
 *   D-RA3 iwx_tx() puts monitor frames on sc->first_data_qid, which is
 *         only set by iwx_enable_mgmt_queue() (not called in monitor
 *         mode, so 0 = the command queue). The port uses the queue iwx
 *         enabled for the monitor station: IWX_DQA_INJECT_MONITOR_QUEUE.
 *   D-RA4 M3 is not in iwx (no IBSS support). It follows Linux
 *         iwl_mvm_start_ap_ibss() for device_family > 22000: MAC context
 *         (FW_MAC_TYPE_IBSS) -> BEACON_TEMPLATE -> BINDING -> multicast
 *         station (+CAB queue) -> broadcast station (+queue), API_STA_TYPE
 *         order. Station ids / queues are fixed (iwx style, no dynamic
 *         allocation): bcast sta 2 on queue 1, mcast sta 3 on queue 2.
 *         TIME_QUOTA is not sent (single binding; -77 firmware schedules
 *         quotas itself, DYNAMIC_QUOTA), no session protection.
 *   D-RA5 M3 keeps promiscuous reception (Owly runs a monitor interface
 *         next to its IBSS cell): PROMISC | CONTROL_AND_MGMT | IN_CRC32 are
 *         added to Linux's IBSS filter flags unless IWX_RADIO_F_NO_PROMISC.
 *         Linux would use a second (LISTENER) MAC context for this.
 *   D-RA6 the IBSS beacon template is built here (mac80211 builds it in
 *         Linux): SSID "owly", 6/12/24 Mb/s basic OFDM rates, DS and IBSS
 *         parameter sets, interval IWX_RADIO_DEFAULT_BINT (10000 TU, Owly)
 *         unless given, sent at 6 Mb/s OFDM. No HT IEs.
 *   D-RA7 any failed teardown command makes iwx_radio_stop() fall back to
 *         iwx_stop() (device down, every queued frame reported flushed);
 *         iwx would leave the firmware in an unknown state.
 *
 * Injection (D-TX*):
 *   D-TX1 frames come from user mode, not net80211: no encryption, no
 *         rate control (TX_FLAGS_CMD_RATE always, explicit rate_n_flags),
 *         TX_FLAGS_ENCRYPT_DIS always. Legacy rates as iwx_tx_fill_cmd()
 *         (antenna A); HT MCS as Linux iwl_mvm_get_inject_tx_rate()
 *         (MCS 8-15 on antennas A+B).
 *   D-TX2 the TX_CMD gen3 API has no "no ACK" flag (Linux ignores
 *         IEEE80211_TX_CTL_NO_ACK on the new TX API). IWX_TX_F_NOACK sets
 *         the QoS Ack Policy to "No Ack" in unicast QoS data frames; other
 *         unicast frames are sent as given and flagged
 *         IWX_TXR_F_NOACK_NOT_HONORED. Group-addressed frames are never
 *         acknowledged anyway.
 *   D-TX3 payload bytes are copied into a per-queue DMA buffer of
 *         IWX_TX_MAX_INFLIGHT slots (no mbuf DMA mapping), slot = ring
 *         index % IWX_TX_MAX_INFLIGHT; at most IWX_TX_MAX_INFLIGHT frames
 *         are queued (iwx: IWX_TX_RING_HIMARK, 224).
 *   D-TX4 every accepted frame gets exactly one osdep_tx_status(): from
 *         its TX_CMD response, as IWX_TXST_NO_STATUS/FLUSHED when a later
 *         SSN or a TXPATH_FLUSH frees it, or FLUSHED when the queue is
 *         reset (radio stop, device stop, fatal error).
 */

#include "iwx_var.h"

/* local helper replacing ieee80211_get_hdrlen() (802.11-2020 9.2) */
static uint32_t
iwx_80211_hdrlen_full(const uint8_t *fc)
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
	if (fc[1] & 0x80 &&
	    (type == IEEE80211_FC0_TYPE_MGT || (subtype & 0x80)))
		hdrlen += 4;			/* +HTC (order bit) */
	return hdrlen;
}

static uint32_t
iwx_80211_hdrlen(const uint8_t *fc, uint32_t len)
{
	uint32_t hdrlen = iwx_80211_hdrlen_full(fc);

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

/* D-RX6: decode firmware rate_n_flags (v1: iwx/Linux _V1 macros; v2). */
static void
iwx_rx_decode_rate(struct iwx_softc *sc, struct iwx_rx_meta *m)
{
	static const uint16_t ofdm_rate[8] = { 12, 18, 24, 36, 48, 72, 96, 108 };
	static const uint16_t cck_rate[4] = { 2, 4, 11, 22 };
	uint32_t r = m->rate_n_flags;

	m->mcs = 0xff;
	m->nss = 1;
	if (r & IWX_RATE_MCS_STBC_MSK)
		m->flags |= IWX_RXMETA_F_STBC;

	if (sc->sc_rate_n_flags_version >= 2) {
		m->bw = (uint8_t)((r & IWX_RATE_MCS_CHAN_WIDTH_MSK) >>
		    IWX_RATE_MCS_CHAN_WIDTH_POS);
		if (r & IWX_RATE_MCS_LDPC_MSK)
			m->flags |= IWX_RXMETA_F_LDPC;
		switch (r & IWX_RATE_MCS_MOD_TYPE_MSK) {
		case IWX_RATE_MCS_CCK_MSK:
			m->encoding = IWX_RXMETA_ENC_CCK;
			if ((r & IWX_RATE_LEGACY_RATE_MSK) < nitems(cck_rate))
				m->legacy_rate =
				    cck_rate[r & IWX_RATE_LEGACY_RATE_MSK];
			break;
		case IWX_RATE_MCS_LEGACY_OFDM_MSK:
			m->encoding = IWX_RXMETA_ENC_OFDM;
			m->legacy_rate = ofdm_rate[r & IWX_RATE_LEGACY_RATE_MSK];
			break;
		case IWX_RATE_MCS_HT_MSK:
			m->encoding = IWX_RXMETA_ENC_HT;
			m->mcs = (uint8_t)IWX_RATE_HT_MCS_INDEX(r);
			m->nss = (uint8_t)(((r & IWX_RATE_MCS_NSS_MSK) >>
			    IWX_RATE_MCS_NSS_POS) + 1);
			if (r & IWX_RATE_MCS_SGI_MSK)
				m->flags |= IWX_RXMETA_F_SGI;
			break;
		default:	/* VHT, HE, EHT: code + 1-bit NSS */
			m->encoding = (uint8_t)((r & IWX_RATE_MCS_MOD_TYPE_MSK) >>
			    IWX_RATE_MCS_MOD_TYPE_POS);
			m->mcs = (uint8_t)(r & IWX_RATE_MCS_CODE_MSK);
			m->nss = (uint8_t)(((r & IWX_RATE_MCS_NSS_MSK) >>
			    IWX_RATE_MCS_NSS_POS) + 1);
			if (m->encoding == IWX_RXMETA_ENC_VHT &&
			    (r & IWX_RATE_MCS_SGI_MSK))
				m->flags |= IWX_RXMETA_F_SGI;
			break;
		}
		return;
	}

	/* rate_n_flags v1 */
	m->bw = (uint8_t)((r & IWX_RATE_MCS_CHAN_WIDTH_MSK_V1) >>
	    IWX_RATE_MCS_CHAN_WIDTH_POS);
	if (r & IWX_RATE_MCS_LDPC_MSK_V1)
		m->flags |= IWX_RXMETA_F_LDPC;
	if (r & IWX_RATE_MCS_SGI_MSK_V1)
		m->flags |= IWX_RXMETA_F_SGI;
	if (r & IWX_RATE_MCS_HT_MSK_V1) {
		m->encoding = IWX_RXMETA_ENC_HT;
		m->mcs = (uint8_t)(r & 0x1f);	/* rate code | NSS << 3 */
		m->nss = (uint8_t)(((r >> 3) & 0x3) + 1);
	} else if (r & IWX_RATE_MCS_VHT_MSK_V1) {
		m->encoding = IWX_RXMETA_ENC_VHT;
		m->mcs = (uint8_t)(r & IWX_RATE_VHT_MCS_RATE_CODE_MSK);
		m->nss = (uint8_t)(((r & IWX_RATE_VHT_MCS_NSS_MSK) >>
		    IWX_RATE_VHT_MCS_NSS_POS) + 1);
	} else if (r & (1u << IWX_RATE_MCS_HE_POS_V1)) {
		m->encoding = IWX_RXMETA_ENC_HE;
		m->mcs = (uint8_t)(r & IWX_RATE_VHT_MCS_RATE_CODE_MSK);
		m->nss = (uint8_t)(((r & IWX_RATE_VHT_MCS_NSS_MSK) >>
		    IWX_RATE_VHT_MCS_NSS_POS) + 1);
		m->flags &= (uint16_t)~IWX_RXMETA_F_SGI;
	} else if (r & IWX_RATE_MCS_CCK_MSK_V1) {
		m->encoding = IWX_RXMETA_ENC_CCK;
		/* PLCP 10/20/55/110 = rate in 100 kb/s */
		m->legacy_rate = (uint16_t)((r & IWX_RATE_LEGACY_RATE_MSK_V1) / 5);
	} else {
		static const struct { uint8_t plcp, rate; } op[8] = {
			{ 0xd, 12 }, { 0xf, 18 }, { 0x5, 24 }, { 0x7, 36 },
			{ 0x9, 48 }, { 0xb, 72 }, { 0x1, 96 }, { 0x3, 108 },
		};
		size_t i;

		m->encoding = IWX_RXMETA_ENC_OFDM;
		for (i = 0; i < nitems(op); i++)
			if ((r & IWX_RATE_LEGACY_RATE_MSK_V1) == op[i].plcp)
				m->legacy_rate = op[i].rate;
	}
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
	uint32_t len, hdrlen, crc;
	int rssi, bad_fcs;
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

	bad_fcs = (!(desc->status & htole32(IWX_RX_MPDU_RES_STATUS_CRC_OK)) ||
	    !(desc->status & htole32(IWX_RX_MPDU_RES_STATUS_OVERRUN_OK)));
	if (bad_fcs) {
		sc->sc_stat_rx_bad_fcs++;
		if (!sc->sc_rx_keep_bad_fcs) {	/* D-RX5 */
			sc->sc_stat_rx_dropped++;
			return; /* drop */
		}
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
	meta.orig_len = len;
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
	if (bad_fcs)
		meta.flags |= IWX_RXMETA_F_BAD_FCS;
	iwx_rx_decode_rate(sc, &meta);
	if (meta.encoding == IWX_RXMETA_ENC_CCK &&
	    (meta.phy_info & IWX_RX_MPDU_PHY_SHORT_PREAMBLE))
		meta.flags |= IWX_RXMETA_F_SHORTPRE;
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210 &&
	    !(meta.phy_info & IWX_RX_MPDU_PHY_TSF_OVERLOAD)) {
		meta.tsf = ((uint64_t)le32toh(desc->v3.tsf_on_air_rise1) << 32) |
		    le32toh(desc->v3.tsf_on_air_rise0);
		meta.flags |= IWX_RXMETA_F_TSF_VALID;
	}
	if (desc->mac_flags2 & IWX_RX_MPDU_MFLG2_AMSDU)
		meta.flags |= IWX_RXMETA_F_AMSDU;
	/* Linux iwl_mvm_rx_mpdu_mq(): A-MPDU reference for monitor users */
	if (meta.phy_info & IWX_RX_MPDU_PHY_AMPDU) {
		int toggle = (meta.phy_info & IWX_RX_MPDU_PHY_AMPDU_TOGGLE) != 0;

		meta.flags |= IWX_RXMETA_F_AMPDU;
		if (toggle != sc->sc_ampdu_toggle || sc->sc_ampdu_ref == 0) {
			sc->sc_ampdu_ref++;
			if (sc->sc_ampdu_ref == 0)
				sc->sc_ampdu_ref++;
			sc->sc_ampdu_toggle = toggle;
		}
		meta.ampdu_ref = sc->sc_ampdu_ref;
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

	/* D-RX4: FCS (monitor, IN_CRC32) or MIC/CRC bytes left by the RADA */
	if (sc->sc_rx_fcs_included)
		crc = 4;		/* IEEE80211_FCS_LEN */
	else
		crc = (uint32_t)((desc->mac_flags1 &
		    IWX_RX_MPDU_MFLG1_MIC_CRC_LEN_MASK) >>
		    IWX_RX_MPDU_MFLG1_MIC_CRC_LEN_SHIFT);
	if (crc != 0) {
		if (len < crc + IEEE80211_MIN_FRAME_LEN) {
			sc->sc_stat_rx_dropped++;
			return;
		}
		len -= crc;
		meta.flags |= IWX_RXMETA_F_FCS_STRIPPED;
	}

	sc->sc_stat_rx_frames++;
	osdep_rx_frame(sc->sc_os, &meta, frame, len);
}

/* ---- M2 / M3: firmware contexts ----------------------------------------- */

#define IWX_RADIO_MAC_ID	0	/* in->in_id */
#define IWX_RADIO_MAC_COLOR	0	/* in->in_color */

static int
iwx_radio_is_2ghz(uint8_t chan)
{
	return chan <= 14;
}

/* iwx_mimo_enabled(); no IEEE80211_F_NOMIMO user flag here */
static int
iwx_mimo_enabled(struct iwx_softc *sc)
{
	return !sc->sc_nvm.sku_cap_mimo_disable;
}

static uint32_t
iwx_radio_lmac_id(struct iwx_softc *sc, uint8_t chan)
{
	if (iwx_radio_is_2ghz(chan) ||
	    !isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_CDB_SUPPORT))
		return IWX_LMAC_24G_INDEX;
	return IWX_LMAC_5G_INDEX;
}

static uint32_t
iwx_rx_chain_info(struct iwx_softc *sc, uint8_t chains_static,
    uint8_t chains_dynamic)
{
	uint32_t v;

	v = (uint32_t)iwx_fw_valid_rx_ant(sc) << IWX_PHY_RX_CHAIN_VALID_POS;
	v |= (uint32_t)chains_static << IWX_PHY_RX_CHAIN_CNT_POS;
	v |= (uint32_t)chains_dynamic << IWX_PHY_RX_CHAIN_MIMO_CNT_POS;
	return v;
}

/*
 * iwx_phy_ctxt_cmd() / iwx_phy_ctxt_cmd_uhb_v3_v4() / _v3_v4() for a
 * 20 MHz channel (sco SCN, vht_chan_width HT): width MODE20, ctrl_pos
 * 1_BELOW.
 */
static int
iwx_radio_phy_cmd(struct iwx_softc *sc, uint8_t chains_static,
    uint8_t chains_dynamic, uint32_t action)
{
	struct iwx_phy_ctxt *ctxt = &sc->sc_phyctxt[0];
	int cmdver, rlc2;
	uint32_t rxchain = 0;

	cmdver = iwx_lookup_cmd_ver(sc, IWX_LONG_GROUP, IWX_PHY_CONTEXT_CMD);
	if (cmdver != 3 && cmdver != 4) {
		IWX_ERR(sc, "firmware does not support phy-context-cmd v3/v4\n");
		return ENOTSUP;
	}
	rlc2 = iwx_lookup_cmd_ver(sc, IWX_DATA_PATH_GROUP,
	    IWX_RLC_CONFIG_CMD) == 2;
	if (!rlc2)
		rxchain = iwx_rx_chain_info(sc, chains_static, chains_dynamic);

	if (isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_ULTRA_HB_CHANNELS)) {
		struct iwx_phy_context_cmd_uhb cmd;

		memset(&cmd, 0, sizeof(cmd));
		cmd.id_and_color = htole32(IWX_FW_CMD_ID_AND_COLOR(ctxt->id,
		    ctxt->color));
		cmd.action = htole32(action);
		cmd.lmac_id = htole32(iwx_radio_lmac_id(sc, ctxt->channel));
		cmd.ci.band = iwx_radio_is_2ghz(ctxt->channel) ?
		    IWX_PHY_BAND_24 : IWX_PHY_BAND_5;
		cmd.ci.channel = htole32(ctxt->channel);
		cmd.ci.width = IWX_PHY_VHT_CHANNEL_MODE20;
		cmd.ci.ctrl_pos = IWX_PHY_VHT_CTRL_POS_1_BELOW;
		cmd.rxchain_info = htole32(rxchain);
		return iwx_send_cmd_pdu(sc, IWX_PHY_CONTEXT_CMD, 0,
		    sizeof(cmd), &cmd);
	} else {
		struct iwx_phy_context_cmd cmd;

		memset(&cmd, 0, sizeof(cmd));
		cmd.id_and_color = htole32(IWX_FW_CMD_ID_AND_COLOR(ctxt->id,
		    ctxt->color));
		cmd.action = htole32(action);
		cmd.lmac_id = htole32(iwx_radio_lmac_id(sc, ctxt->channel));
		cmd.ci.band = iwx_radio_is_2ghz(ctxt->channel) ?
		    IWX_PHY_BAND_24 : IWX_PHY_BAND_5;
		cmd.ci.channel = ctxt->channel;
		cmd.ci.width = IWX_PHY_VHT_CHANNEL_MODE20;
		cmd.ci.ctrl_pos = IWX_PHY_VHT_CTRL_POS_1_BELOW;
		cmd.rxchain_info = htole32(rxchain);
		return iwx_send_cmd_pdu(sc, IWX_PHY_CONTEXT_CMD, 0,
		    sizeof(cmd), &cmd);
	}
}

/* iwx_phy_send_rlc() */
static int
iwx_radio_send_rlc(struct iwx_softc *sc, uint8_t chains_static,
    uint8_t chains_dynamic)
{
	struct iwx_rlc_config_cmd cmd;

	if (iwx_lookup_cmd_ver(sc, IWX_DATA_PATH_GROUP,
	    IWX_RLC_CONFIG_CMD) != 2)
		return 0;
	memset(&cmd, 0, sizeof(cmd));
	cmd.phy_id = htole32(sc->sc_phyctxt[0].id);
	cmd.rlc.rx_chain_info = htole32(iwx_rx_chain_info(sc, chains_static,
	    chains_dynamic));
	return iwx_send_cmd_pdu(sc, iwx_cmd_id(IWX_RLC_CONFIG_CMD,
	    IWX_DATA_PATH_GROUP, 2), 0, sizeof(cmd), &cmd);
}

/* iwx_phy_ctxt_update() for an unchanged channel: MODIFY + RLC */
static int
iwx_radio_phy_update(struct iwx_softc *sc, uint8_t chains)
{
	int err;

	err = iwx_radio_phy_cmd(sc, chains, chains,
	    IWX_FW_CTXT_ACTION_MODIFY);
	if (err) {
		IWX_ERR(sc, "could not update PHY context (error %d)\n", err);
		return err;
	}
	return iwx_radio_send_rlc(sc, chains, chains);
}

/* EDCA parameters of OpenBSD net80211 / mac80211 for 11g (ecw, aifsn, txop) */
static const struct {
	uint8_t ecwmin, ecwmax, aifsn;
	uint16_t txop;		/* units of 32 us */
} iwx_radio_edca[IWX_AC_NUM] = {
	{ 4, 10, 3, 0 },	/* BE */
	{ 4, 10, 7, 0 },	/* BK */
	{ 3, 4, 2, 94 },	/* VI */
	{ 2, 3, 2, 47 },	/* VO */
};
/* iwx_ac_to_tx_fifo[] */
static const uint8_t iwx_radio_ac_to_tx_fifo[IWX_AC_NUM] = {
	IWX_GEN2_EDCA_TX_FIFO_BE,
	IWX_GEN2_EDCA_TX_FIFO_BK,
	IWX_GEN2_EDCA_TX_FIFO_VI,
	IWX_GEN2_EDCA_TX_FIFO_VO,
};

/*
 * MAC_CONTEXT_CMD.
 * LISTENER: iwx_mac_ctxt_cmd_common() + iwx_mac_ctxt_cmd() for
 *   IEEE80211_M_MONITOR (D-RA1 values for the sta part).
 * IBSS: Linux iwl_mvm_mac_ctxt_cmd_common() + iwl_mvm_mac_ctxt_cmd_ibss()
 *   (D-RA4/5): ack rates for basic rates {6, 12, 24} OFDM computed with
 *   iwx_ack_rates() rules, 11g EDCA, QoS + HT (TGN).
 */
void
iwx_radio_fill_mac_cmd(struct iwx_softc *sc, struct iwx_mac_ctx_cmd *cmd,
    uint32_t action, int assoc)
{
	int i;

	memset(cmd, 0, sizeof(*cmd));
	cmd->id_and_color = htole32(IWX_FW_CMD_ID_AND_COLOR(IWX_RADIO_MAC_ID,
	    IWX_RADIO_MAC_COLOR));
	cmd->action = htole32(action);
	if (action == IWX_FW_CTXT_ACTION_REMOVE)
		return;

	cmd->tsf_id = htole32(IWX_TSF_ID_A);
	memcpy(cmd->node_addr, sc->sc_macaddr, ETHER_ADDR_LEN);

	if (sc->sc_radio_mode == IWX_RADIO_MODE_LISTENER) {
		cmd->mac_type = htole32(IWX_FW_MAC_TYPE_LISTENER);
		memset(cmd->bssid_addr, 0xff, ETHER_ADDR_LEN);
		cmd->filter_flags = htole32(IWX_MAC_FILTER_IN_PROMISC |
		    IWX_MAC_FILTER_IN_CONTROL_AND_MGMT |
		    IWX_MAC_FILTER_ACCEPT_GRP |
		    IWX_MAC_FILTER_IN_BEACON |
		    IWX_MAC_FILTER_IN_PROBE_REQUEST |
		    IWX_MAC_FILTER_IN_CRC32);
		/* iwx_mac_ctxt_cmd_fill_sta() with a zeroed ic_bss */
		cmd->sta.is_assoc = htole32(assoc);
		cmd->sta.bi = htole32(0);
		cmd->sta.dtim_interval = htole32(0);
		cmd->sta.data_policy = htole32(0);
		cmd->sta.listen_interval = htole32(10);
		cmd->sta.assoc_id = htole32(0);
		return;
	}

	cmd->mac_type = htole32(IWX_FW_MAC_TYPE_IBSS);
	memcpy(cmd->bssid_addr, sc->sc_bssid, ETHER_ADDR_LEN);
	cmd->cck_rates = htole32(0x01);		/* 1M (mandatory) */
	cmd->ofdm_rates = htole32(0x15);	/* 6M, 12M, 24M */
	cmd->cck_short_preamble = htole32(IWX_MAC_FLG_SHORT_PREAMBLE);
	cmd->short_slot = htole32(0);
	for (i = 0; i < IWX_AC_NUM; i++) {
		int txf = iwx_radio_ac_to_tx_fifo[i];

		cmd->ac[txf].cw_min = htole16((uint16_t)((1 <<
		    iwx_radio_edca[i].ecwmin) - 1));
		cmd->ac[txf].cw_max = htole16((uint16_t)((1 <<
		    iwx_radio_edca[i].ecwmax) - 1));
		cmd->ac[txf].aifsn = iwx_radio_edca[i].aifsn;
		cmd->ac[txf].fifos_mask = (uint8_t)(1 << txf);
		cmd->ac[txf].edca_txop = htole16((uint16_t)
		    (iwx_radio_edca[i].txop * 32));
	}
	cmd->qos_flags = htole32(IWX_MAC_QOS_FLG_UPDATE_EDCA |
	    IWX_MAC_QOS_FLG_TGN);
	cmd->filter_flags = htole32(IWX_MAC_FILTER_IN_BEACON |
	    IWX_MAC_FILTER_IN_PROBE_REQUEST | IWX_MAC_FILTER_ACCEPT_GRP);
	if (!(sc->sc_radio_flags & IWX_RADIO_F_NO_PROMISC))
		cmd->filter_flags |= htole32(IWX_MAC_FILTER_IN_PROMISC |
		    IWX_MAC_FILTER_IN_CONTROL_AND_MGMT |
		    IWX_MAC_FILTER_IN_CRC32);
	cmd->ibss.bi = htole32(sc->sc_beacon_int);
	cmd->ibss.beacon_template = htole32(IWX_RADIO_MAC_ID);
}

static int
iwx_radio_mac_cmd(struct iwx_softc *sc, uint32_t action, int assoc)
{
	struct iwx_mac_ctx_cmd cmd;
	int active = (sc->sc_flags & IWX_FLAG_MAC_ACTIVE) != 0;

	if (action == IWX_FW_CTXT_ACTION_ADD && active)
		return EBUSY;		/* iwx: panic("MAC already added") */
	if (action == IWX_FW_CTXT_ACTION_REMOVE && !active)
		return EINVAL;		/* iwx: panic("MAC already removed") */
	iwx_radio_fill_mac_cmd(sc, &cmd, action, assoc);
	return iwx_send_cmd_pdu(sc, IWX_MAC_CONTEXT_CMD, 0, sizeof(cmd), &cmd);
}

/* iwx_binding_cmd() */
static int
iwx_radio_binding_cmd(struct iwx_softc *sc, uint32_t action)
{
	struct iwx_binding_cmd cmd;
	struct iwx_phy_ctxt *phyctxt = &sc->sc_phyctxt[0];
	uint32_t status;
	int i, err;

	memset(&cmd, 0, sizeof(cmd));
	cmd.id_and_color = htole32(IWX_FW_CMD_ID_AND_COLOR(phyctxt->id,
	    phyctxt->color));
	cmd.action = htole32(action);
	cmd.phy = htole32(IWX_FW_CMD_ID_AND_COLOR(phyctxt->id,
	    phyctxt->color));
	cmd.macs[0] = htole32(IWX_FW_CMD_ID_AND_COLOR(IWX_RADIO_MAC_ID,
	    IWX_RADIO_MAC_COLOR));
	for (i = 1; i < IWX_MAX_MACS_IN_BINDING; i++)
		cmd.macs[i] = htole32(IWX_FW_CTXT_INVALID);
	cmd.lmac_id = htole32(iwx_radio_lmac_id(sc, phyctxt->channel));

	status = 0;
	err = iwx_send_cmd_pdu_status(sc, IWX_BINDING_CONTEXT_CMD,
	    sizeof(cmd), &cmd, &status);
	if (err == 0 && status != 0)
		err = EIO;
	return err;
}

/*
 * ADD_STA (v10..v12 layout).
 * M2: iwx_add_sta_cmd() for IEEE80211_M_MONITOR (no HT node flags).
 * M3: Linux iwl_mvm_add_int_sta_common() for the multicast and broadcast
 *     stations (station_type from API_STA_TYPE, tfd_queue_msk unused with
 *     the new TX API).
 */
void
iwx_radio_fill_add_sta(struct iwx_softc *sc, struct iwx_add_sta_cmd *cmd,
    int sta_id, int station_type, const uint8_t *addr, int update)
{
	memset(cmd, 0, sizeof(*cmd));
	cmd->sta_id = (uint8_t)sta_id;
	cmd->station_type = (uint8_t)station_type;
	cmd->mac_id_n_color = htole32(IWX_FW_CMD_ID_AND_COLOR(IWX_RADIO_MAC_ID,
	    IWX_RADIO_MAC_COLOR));
	if (!update && addr != NULL)
		memcpy(cmd->addr, addr, ETHER_ADDR_LEN);
	cmd->add_modify = update ? 1 : 0;
	if (sc->sc_radio_mode == IWX_RADIO_MODE_LISTENER)
		cmd->station_flags_msk |= htole32(IWX_STA_FLG_FAT_EN_MSK |
		    IWX_STA_FLG_MIMO_EN_MSK);
	else
		/* Linux internal stations: tid_disable_tx all but ours */
		cmd->tid_disable_tx = htole16(0xffff);
}

static int
iwx_radio_add_sta(struct iwx_softc *sc, int sta_id, int station_type,
    const uint8_t *addr, int update)
{
	struct iwx_add_sta_cmd cmd;
	uint32_t status;
	int err;

	iwx_radio_fill_add_sta(sc, &cmd, sta_id, station_type, addr, update);
	status = IWX_ADD_STA_SUCCESS;
	err = iwx_send_cmd_pdu_status(sc, IWX_ADD_STA, sizeof(cmd), &cmd,
	    &status);
	if (!err && (status & IWX_ADD_STA_STATUS_MASK) != IWX_ADD_STA_SUCCESS)
		err = EIO;
	return err;
}

/* iwx_rm_sta_cmd() */
static int
iwx_radio_rm_sta_cmd(struct iwx_softc *sc, int sta_id)
{
	struct iwx_rm_sta_cmd cmd;

	memset(&cmd, 0, sizeof(cmd));
	cmd.sta_id = (uint8_t)sta_id;
	return iwx_send_cmd_pdu(sc, IWX_REMOVE_STA, 0, sizeof(cmd), &cmd);
}

/* iwx_drain_sta() (D-RA2: real station id) */
static int
iwx_radio_drain_sta(struct iwx_softc *sc, int sta_id, int drain)
{
	struct iwx_add_sta_cmd cmd;
	uint32_t status;
	int err;

	memset(&cmd, 0, sizeof(cmd));
	cmd.mac_id_n_color = htole32(IWX_FW_CMD_ID_AND_COLOR(IWX_RADIO_MAC_ID,
	    IWX_RADIO_MAC_COLOR));
	cmd.sta_id = (uint8_t)sta_id;
	cmd.add_modify = IWX_STA_MODE_MODIFY;
	cmd.station_flags = drain ? htole32(IWX_STA_FLG_DRAIN_FLOW) : 0;
	cmd.station_flags_msk = htole32(IWX_STA_FLG_DRAIN_FLOW);

	status = IWX_ADD_STA_SUCCESS;
	err = iwx_send_cmd_pdu_status(sc, IWX_ADD_STA, sizeof(cmd), &cmd,
	    &status);
	if (err) {
		IWX_ERR(sc, "could not update sta (error %d)\n", err);
		return err;
	}
	if ((status & IWX_ADD_STA_STATUS_MASK) != IWX_ADD_STA_SUCCESS) {
		IWX_ERR(sc, "couldn't %s draining for station\n",
		    drain ? "enable" : "disable");
		return EIO;
	}
	return 0;
}

static void iwx_txq_advance(struct iwx_softc *, struct iwx_tx_ring *,
    uint16_t, uint32_t);

/* iwx_flush_sta_tids() */
static int
iwx_radio_flush_sta_tids(struct iwx_softc *sc, int sta_id, uint16_t tids)
{
	struct iwx_rx_packet *pkt;
	struct iwx_tx_path_flush_cmd_rsp *resp;
	struct iwx_tx_path_flush_cmd flush_cmd;
	struct iwx_host_cmd hcmd;
	int err, i, num_flushed_queues;
	size_t resp_len;

	memset(&flush_cmd, 0, sizeof(flush_cmd));
	flush_cmd.sta_id = htole32((uint32_t)sta_id);
	flush_cmd.tid_mask = htole16(tids);
	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.id = IWX_TXPATH_FLUSH;
	hcmd.len[0] = sizeof(flush_cmd);
	hcmd.data[0] = &flush_cmd;
	hcmd.flags = IWX_CMD_WANT_RESP;
	hcmd.resp_pkt_len = sizeof(*pkt) + sizeof(*resp);

	err = iwx_send_cmd(sc, &hcmd);
	if (err)
		return err;

	pkt = hcmd.resp_pkt;
	if (!pkt || (pkt->hdr.flags & IWX_CMD_FAILED_MSK)) {
		err = EIO;
		goto out;
	}
	resp_len = iwx_rx_packet_payload_len(pkt);
	if (resp_len != sizeof(*resp)) {
		err = EIO;
		goto out;
	}
	resp = (void *)pkt->data;
	if (le16toh(resp->sta_id) != sta_id) {
		err = EIO;
		goto out;
	}
	num_flushed_queues = le16toh(resp->num_flushed_queues);
	if (num_flushed_queues > IWX_TX_FLUSH_QUEUE_RSP) {
		err = EIO;
		goto out;
	}
	for (i = 0; i < num_flushed_queues; i++) {
		struct iwx_flush_queue_info *queue_info = &resp->queues[i];
		uint16_t tid = le16toh(queue_info->tid);
		uint16_t read_after = le16toh(queue_info->read_after_flush);
		uint16_t qid = le16toh(queue_info->queue_num);
		struct iwx_tx_ring *txq;

		if (qid == IWX_DQA_CMD_QUEUE || qid >= nitems(sc->txq))
			continue;
		txq = &sc->txq[qid];
		if (!txq->allocated || tid != txq->tid)
			continue;
		osdep_lock(sc->sc_os);
		iwx_txq_advance(sc, txq, read_after, IWX_TXST_FLUSHED);
		osdep_unlock(sc->sc_os);
	}
out:
	iwx_free_resp(sc, &hcmd);
	return err;
}

/* iwx_flush_sta(): drain, flush all TIDs, undrain (D-RA2) */
static int
iwx_radio_flush_sta(struct iwx_softc *sc, int sta_id)
{
	int err;

	err = iwx_radio_drain_sta(sc, sta_id, 1);
	if (err)
		return err;
	err = iwx_radio_flush_sta_tids(sc, sta_id, 0xffff);
	if (err) {
		IWX_ERR(sc, "could not flush Tx path (error %d)\n", err);
		return err;
	}
	return iwx_radio_drain_sta(sc, sta_id, 0);
}

/* data queue: TFD ring + byte count table (D-TR4) + payload slots (D-TX3) */
static int
iwx_radio_alloc_txq(struct iwx_softc *sc, int qid)
{
	struct iwx_tx_ring *ring = &sc->txq[qid];
	int err;

	if (!ring->allocated) {
		err = iwx_alloc_tx_ring(sc, ring, qid);
		if (err)
			return err;
	}
	if (ring->pay_dma.vaddr == NULL) {
		err = iwx_dma_contig_alloc(sc, &ring->pay_dma,
		    (size_t)IWX_TX_MAX_INFLIGHT * IWX_TX_PAYLOAD_SLOT, 64);
		if (err)
			return err;
	}
	return 0;
}

/* iwx_enable_txq() (SCD_QUEUE_CONFIG v3 or the old SCD_QUEUE_CFG) */
static int
iwx_radio_enable_txq(struct iwx_softc *sc, int sta_id, int qid, int tid,
    int num_slots)
{
	struct iwx_rx_packet *pkt;
	struct iwx_tx_queue_cfg_rsp *resp;
	struct iwx_tx_queue_cfg_cmd cmd_v0;
	struct iwx_scd_queue_cfg_cmd cmd_v3;
	struct iwx_host_cmd hcmd;
	struct iwx_tx_ring *ring = &sc->txq[qid];
	int err, fwqid, cmd_ver;
	uint32_t wr_idx;
	size_t resp_len;

	err = iwx_radio_alloc_txq(sc, qid);
	if (err)
		return err;

	osdep_lock(sc->sc_os);
	iwx_reset_tx_ring(sc, ring);
	osdep_unlock(sc->sc_os);

	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.flags = IWX_CMD_WANT_RESP;
	hcmd.resp_pkt_len = sizeof(*pkt) + sizeof(*resp);

	cmd_ver = iwx_lookup_cmd_ver(sc, IWX_DATA_PATH_GROUP,
	    IWX_SCD_QUEUE_CONFIG_CMD);
	if (cmd_ver == 0 || cmd_ver == IWX_FW_CMD_VER_UNKNOWN) {
		memset(&cmd_v0, 0, sizeof(cmd_v0));
		cmd_v0.sta_id = (uint8_t)sta_id;
		cmd_v0.tid = (uint8_t)tid;
		cmd_v0.flags = htole16(IWX_TX_QUEUE_CFG_ENABLE_QUEUE);
		cmd_v0.cb_size = htole32(IWX_TFD_QUEUE_CB_SIZE(num_slots));
		cmd_v0.byte_cnt_addr = htole64(ring->bc_tbl.paddr);
		cmd_v0.tfdq_addr = htole64(ring->desc_dma.paddr);
		hcmd.id = IWX_SCD_QUEUE_CFG;
		hcmd.data[0] = &cmd_v0;
		hcmd.len[0] = sizeof(cmd_v0);
	} else if (cmd_ver == 3) {
		memset(&cmd_v3, 0, sizeof(cmd_v3));
		cmd_v3.operation = htole32(IWX_SCD_QUEUE_ADD);
		cmd_v3.u.add.tfdq_dram_addr = htole64(ring->desc_dma.paddr);
		cmd_v3.u.add.bc_dram_addr = htole64(ring->bc_tbl.paddr);
		cmd_v3.u.add.cb_size = htole32(IWX_TFD_QUEUE_CB_SIZE(num_slots));
		cmd_v3.u.add.flags = htole32(0);
		cmd_v3.u.add.sta_mask = htole32(1u << sta_id);
		cmd_v3.u.add.tid = (uint8_t)tid;
		hcmd.id = IWX_WIDE_ID(IWX_DATA_PATH_GROUP,
		    IWX_SCD_QUEUE_CONFIG_CMD);
		hcmd.data[0] = &cmd_v3;
		hcmd.len[0] = sizeof(cmd_v3);
	} else {
		IWX_ERR(sc, "unsupported SCD_QUEUE_CFG command version %d\n",
		    cmd_ver);
		return ENOTSUP;
	}

	err = iwx_send_cmd(sc, &hcmd);
	if (err)
		return err;

	pkt = hcmd.resp_pkt;
	if (!pkt || (pkt->hdr.flags & IWX_CMD_FAILED_MSK)) {
		err = EIO;
		goto out;
	}
	resp_len = iwx_rx_packet_payload_len(pkt);
	if (resp_len != sizeof(*resp)) {
		err = EIO;
		goto out;
	}
	resp = (void *)pkt->data;
	fwqid = le16toh(resp->queue_number);
	wr_idx = le16toh(resp->write_pointer);

	/*
	 * airdrop: the firmware owns a queue for (sta, tid) from here on,
	 * even if it is not the one we asked for; mark it enabled so the
	 * error path removes it again (SCD_QUEUE_REMOVE is keyed by
	 * sta_mask/tid). iwx would leak it.
	 */
	sc->qenablemsk |= (1 << qid);
	ring->tid = tid;

	/* Unlike iwlwifi, we do not support dynamic queue ID assignment. */
	if (fwqid != qid) {
		err = EIO;
		goto out;
	}
	if (wr_idx != (uint32_t)ring->cur_hw) {
		err = EIO;
		goto out;
	}
out:
	iwx_free_resp(sc, &hcmd);
	return err;
}

/* iwx_disable_txq() */
static int
iwx_radio_disable_txq(struct iwx_softc *sc, int sta_id, int qid, uint8_t tid)
{
	struct iwx_rx_packet *pkt;
	struct iwx_tx_queue_cfg_cmd cmd_v0;
	struct iwx_scd_queue_cfg_cmd cmd_v3;
	struct iwx_host_cmd hcmd;
	struct iwx_tx_ring *ring = &sc->txq[qid];
	int err, cmd_ver;

	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.flags = IWX_CMD_WANT_RESP;
	hcmd.resp_pkt_len = sizeof(*pkt) + sizeof(struct iwx_tx_queue_cfg_rsp);

	cmd_ver = iwx_lookup_cmd_ver(sc, IWX_DATA_PATH_GROUP,
	    IWX_SCD_QUEUE_CONFIG_CMD);
	if (cmd_ver == 0 || cmd_ver == IWX_FW_CMD_VER_UNKNOWN) {
		memset(&cmd_v0, 0, sizeof(cmd_v0));
		cmd_v0.sta_id = (uint8_t)sta_id;
		cmd_v0.tid = tid;
		cmd_v0.flags = htole16(0); /* clear "queue enabled" flag */
		hcmd.id = IWX_SCD_QUEUE_CFG;
		hcmd.data[0] = &cmd_v0;
		hcmd.len[0] = sizeof(cmd_v0);
	} else if (cmd_ver == 3) {
		memset(&cmd_v3, 0, sizeof(cmd_v3));
		cmd_v3.operation = htole32(IWX_SCD_QUEUE_REMOVE);
		cmd_v3.u.remove.sta_mask = htole32(1u << sta_id);
		cmd_v3.u.remove.tid = tid;
		hcmd.id = IWX_WIDE_ID(IWX_DATA_PATH_GROUP,
		    IWX_SCD_QUEUE_CONFIG_CMD);
		hcmd.data[0] = &cmd_v3;
		hcmd.len[0] = sizeof(cmd_v3);
	} else {
		IWX_ERR(sc, "unsupported SCD_QUEUE_CFG command version %d\n",
		    cmd_ver);
		return ENOTSUP;
	}

	err = iwx_send_cmd(sc, &hcmd);
	if (err)
		return err;

	pkt = hcmd.resp_pkt;
	if (!pkt || (pkt->hdr.flags & IWX_CMD_FAILED_MSK)) {
		err = EIO;
		goto out;
	}

	sc->qenablemsk &= ~(1 << qid);
	osdep_lock(sc->sc_os);
	iwx_reset_tx_ring(sc, ring);	/* reports leftovers as flushed */
	osdep_unlock(sc->sc_os);
out:
	iwx_free_resp(sc, &hcmd);
	return err;
}

/* iwx_fill_sf_command() + iwx_sf_config() */
static const uint32_t
iwx_sf_full_timeout_def[IWX_SF_NUM_SCENARIO][IWX_SF_NUM_TIMEOUT_TYPES] = {
	{ IWX_SF_SINGLE_UNICAST_AGING_TIMER_DEF,
	  IWX_SF_SINGLE_UNICAST_IDLE_TIMER_DEF },
	{ IWX_SF_AGG_UNICAST_AGING_TIMER_DEF, IWX_SF_AGG_UNICAST_IDLE_TIMER_DEF },
	{ IWX_SF_MCAST_AGING_TIMER_DEF, IWX_SF_MCAST_IDLE_TIMER_DEF },
	{ IWX_SF_BA_AGING_TIMER_DEF, IWX_SF_BA_IDLE_TIMER_DEF },
	{ IWX_SF_TX_RE_AGING_TIMER_DEF, IWX_SF_TX_RE_IDLE_TIMER_DEF },
};
static const uint32_t
iwx_sf_full_timeout[IWX_SF_NUM_SCENARIO][IWX_SF_NUM_TIMEOUT_TYPES] = {
	{ IWX_SF_SINGLE_UNICAST_AGING_TIMER, IWX_SF_SINGLE_UNICAST_IDLE_TIMER },
	{ IWX_SF_AGG_UNICAST_AGING_TIMER, IWX_SF_AGG_UNICAST_IDLE_TIMER },
	{ IWX_SF_MCAST_AGING_TIMER, IWX_SF_MCAST_IDLE_TIMER },
	{ IWX_SF_BA_AGING_TIMER, IWX_SF_BA_IDLE_TIMER },
	{ IWX_SF_TX_RE_AGING_TIMER, IWX_SF_TX_RE_IDLE_TIMER },
};

/* 'have_node': iwx passes ic_bss for FULL_ON, NULL otherwise */
void
iwx_radio_fill_sf(struct iwx_sf_cfg_cmd *sf_cmd, uint32_t state,
    int have_node)
{
	int i, j;

	memset(sf_cmd, 0, sizeof(*sf_cmd));
	sf_cmd->state = htole32(state);
	sf_cmd->watermark[IWX_SF_LONG_DELAY_ON] = htole32(IWX_SF_W_MARK_SCAN);
	/* ic_bss without IEEE80211_NODE_HT -> legacy watermark */
	sf_cmd->watermark[IWX_SF_FULL_ON] = htole32(have_node ?
	    IWX_SF_W_MARK_LEGACY : IWX_SF_W_MARK_MIMO2);
	for (i = 0; i < IWX_SF_NUM_SCENARIO; i++)
		for (j = 0; j < IWX_SF_NUM_TIMEOUT_TYPES; j++)
			sf_cmd->long_delay_timeouts[i][j] =
			    htole32(IWX_SF_LONG_DELAY_AGING_TIMER);
	if (have_node)
		memcpy(sf_cmd->full_on_timeouts, iwx_sf_full_timeout,
		    sizeof(iwx_sf_full_timeout));
	else
		memcpy(sf_cmd->full_on_timeouts, iwx_sf_full_timeout_def,
		    sizeof(iwx_sf_full_timeout_def));
}

static int
iwx_radio_sf_config(struct iwx_softc *sc, uint32_t new_state)
{
	struct iwx_sf_cfg_cmd sf_cmd;

	if (isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_SMART_FIFO_OFFLOAD))
		return 0;
	iwx_radio_fill_sf(&sf_cmd, new_state, new_state == IWX_SF_FULL_ON);
	return iwx_send_cmd_pdu(sc, IWX_REPLY_SF_CFG_CMD, IWX_CMD_ASYNC,
	    sizeof(sf_cmd), &sf_cmd);
}

/* iwx_allow_mcast(): bssid = in->in_macaddr, zero in monitor mode */
static int
iwx_radio_allow_mcast(struct iwx_softc *sc)
{
	uint8_t buf[roundup(sizeof(struct iwx_mcast_filter_cmd), 4)];
	struct iwx_mcast_filter_cmd *cmd = (void *)buf;

	memset(buf, 0, sizeof(buf));
	cmd->filter_own = 1;
	cmd->port_id = 0;
	cmd->count = 0;
	cmd->pass_all = 1;
	return iwx_send_cmd_pdu(sc, IWX_MCAST_FILTER_CMD, 0, sizeof(buf), buf);
}

/* BEACON_TEMPLATE_CMD v10..v14 header (Linux struct iwl_mac_beacon_cmd) */
struct iwx_mac_beacon_cmd {
	uint16_t byte_cnt;
	uint16_t flags;
	uint32_t short_ssid;
	uint32_t reserved;
	uint32_t link_id;	/* template_id before v13; MAC id here */
	uint32_t tim_idx;
	uint32_t tim_size;
	uint32_t ecsa_offset;
	uint32_t csa_offset;
} __packed;
#define IWX_IBSS_SSID		"owly"
#define IWX_IBSS_BEACON_MAX	96

/* D-RA6: returns the beacon frame length */
uint32_t
iwx_radio_build_beacon(struct iwx_softc *sc, uint8_t *buf, size_t size)
{
	static const uint8_t rates[] = {
		0x8c, 0x12, 0x98, 0x24, 0xb0, 0x48, 0x60, 0x6c
	};
	uint32_t n = 0;

	if (size < IWX_IBSS_BEACON_MAX)
		return 0;
	memset(buf, 0, IWX_IBSS_BEACON_MAX);
	buf[n++] = 0x80;			/* beacon */
	buf[n++] = 0x00;
	n += 2;					/* duration */
	memset(&buf[n], 0xff, ETHER_ADDR_LEN);	/* DA */
	n += ETHER_ADDR_LEN;
	memcpy(&buf[n], sc->sc_macaddr, ETHER_ADDR_LEN);	/* SA */
	n += ETHER_ADDR_LEN;
	memcpy(&buf[n], sc->sc_bssid, ETHER_ADDR_LEN);	/* BSSID */
	n += ETHER_ADDR_LEN;
	n += 2;					/* seq: firmware */
	n += 8;					/* timestamp: firmware */
	buf[n++] = (uint8_t)(sc->sc_beacon_int & 0xff);
	buf[n++] = (uint8_t)(sc->sc_beacon_int >> 8);
	buf[n++] = 0x02;			/* capability: IBSS */
	buf[n++] = 0x00;
	buf[n++] = 0;				/* SSID */
	buf[n++] = (uint8_t)(sizeof(IWX_IBSS_SSID) - 1);
	memcpy(&buf[n], IWX_IBSS_SSID, sizeof(IWX_IBSS_SSID) - 1);
	n += (uint32_t)(sizeof(IWX_IBSS_SSID) - 1);
	buf[n++] = 1;				/* supported rates */
	buf[n++] = (uint8_t)sizeof(rates);
	memcpy(&buf[n], rates, sizeof(rates));
	n += (uint32_t)sizeof(rates);
	buf[n++] = 3;				/* DS parameter set */
	buf[n++] = 1;
	buf[n++] = sc->sc_phyctxt[0].channel;
	buf[n++] = 6;				/* IBSS parameter set */
	buf[n++] = 2;
	buf[n++] = 0;				/* ATIM window */
	buf[n++] = 0;
	return n;
}

/* Linux iwl_mvm_mac_ctxt_send_beacon_v9() for an IBSS (no TIM/FILS/CSA) */
static int
iwx_radio_send_beacon(struct iwx_softc *sc)
{
	uint8_t buf[sizeof(struct iwx_mac_beacon_cmd) + IWX_IBSS_BEACON_MAX];
	struct iwx_mac_beacon_cmd *cmd = (void *)buf;
	uint32_t flen;
	int ver;

	ver = iwx_lookup_cmd_ver(sc, IWX_LONG_GROUP, IWX_BEACON_TEMPLATE_CMD);
	if (ver < 10 || ver > 14) {
		IWX_ERR(sc, "unsupported BEACON_TEMPLATE_CMD version %d\n", ver);
		return ENOTSUP;
	}
	memset(buf, 0, sizeof(buf));
	flen = iwx_radio_build_beacon(sc, buf + sizeof(*cmd),
	    sizeof(buf) - sizeof(*cmd));
	cmd->byte_cnt = htole16((uint16_t)flen);
	/*
	 * 6 Mb/s OFDM: iwl_mvm_mac_ctxt_get_beacon_flags(): no CCK flag,
	 * rate index relative to the first OFDM rate (TX_CMD > 8) = 0.
	 */
	cmd->flags = htole16(0);
	cmd->link_id = htole32(IWX_RADIO_MAC_ID);
	return iwx_send_cmd_pdu(sc, IWX_BEACON_TEMPLATE_CMD, 0,
	    (uint16_t)(sizeof(*cmd) + flen), buf);
}

/* ---- bring-up / teardown ---------------------------------------------- */

/* iwx_auth() + iwx_run() for IEEE80211_M_MONITOR (D-RA1..3) */
static int
iwx_radio_up_listener(struct iwx_softc *sc)
{
	uint8_t chains;
	int err;

	err = iwx_radio_phy_cmd(sc, 1, 1, IWX_FW_CTXT_ACTION_ADD);
	if (err) {
		IWX_ERR(sc, "could not add phy context (error %d)\n", err);
		return err;
	}
	sc->sc_flags |= IWX_FLAG_PHY_ACTIVE;
	err = iwx_radio_send_rlc(sc, 1, 1);
	if (err) {
		IWX_ERR(sc, "could not configure RLC for PHY (error %d)\n", err);
		return err;
	}

	err = iwx_radio_mac_cmd(sc, IWX_FW_CTXT_ACTION_ADD, 0);
	if (err) {
		IWX_ERR(sc, "could not add MAC context (error %d)\n", err);
		return err;
	}
	sc->sc_flags |= IWX_FLAG_MAC_ACTIVE;
	sc->sc_rx_fcs_included = 1;	/* IN_CRC32 */

	err = iwx_radio_binding_cmd(sc, IWX_FW_CTXT_ACTION_ADD);
	if (err) {
		IWX_ERR(sc, "could not add binding (error %d)\n", err);
		return err;
	}
	sc->sc_flags |= IWX_FLAG_BINDING_ACTIVE;

	{
		static const uint8_t anyaddr[ETHER_ADDR_LEN] = { 0 };
		err = iwx_radio_add_sta(sc, IWX_MONITOR_STA_ID,
		    IWX_STA_GENERAL_PURPOSE, anyaddr, 0);
	}
	if (err) {
		IWX_ERR(sc, "could not add sta (error %d)\n", err);
		return err;
	}
	sc->sc_flags |= IWX_FLAG_STA_ACTIVE;

	err = iwx_radio_enable_txq(sc, IWX_MONITOR_STA_ID,
	    IWX_DQA_INJECT_MONITOR_QUEUE, IWX_MGMT_TID, IWX_TX_RING_COUNT);
	if (err) {
		IWX_ERR(sc, "could not enable Tx queue %d (error %d)\n",
		    IWX_DQA_INJECT_MONITOR_QUEUE, err);
		return err;
	}
	sc->sc_tx_qid = IWX_DQA_INJECT_MONITOR_QUEUE;
	sc->sc_tx_sta_id = IWX_MONITOR_STA_ID;

	/* iwx_run(): Rx chains for MIMO, 20 MHz */
	chains = iwx_mimo_enabled(sc) ? 2 : 1;
	err = iwx_radio_phy_update(sc, chains);
	if (err) {
		IWX_ERR(sc, "failed to update PHY\n");
		return err;
	}
	err = iwx_radio_add_sta(sc, IWX_MONITOR_STA_ID,
	    IWX_STA_GENERAL_PURPOSE, NULL, 1);
	if (err) {
		IWX_ERR(sc, "could not update STA (error %d)\n", err);
		return err;
	}
	err = iwx_radio_mac_cmd(sc, IWX_FW_CTXT_ACTION_MODIFY, 1);
	if (err) {
		IWX_ERR(sc, "failed to update MAC\n");
		return err;
	}
	err = iwx_radio_sf_config(sc, IWX_SF_FULL_ON);
	if (err) {
		IWX_ERR(sc, "could not set sf full on (error %d)\n", err);
		return err;
	}
	err = iwx_radio_allow_mcast(sc);
	if (err) {
		IWX_ERR(sc, "could not allow mcast (error %d)\n", err);
		return err;
	}
	/* iwx_set_pslevel() returns 0 in monitor mode */
	return 0;
}

/* Linux iwl_mvm_start_ap_ibss() for an IBSS on device_family > 22000 */
static int
iwx_radio_up_ibss(struct iwx_softc *sc)
{
	static const uint8_t mcast_addr[ETHER_ADDR_LEN] =
	    { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 };	/* Linux _maddr */
	static const uint8_t bcast_addr[ETHER_ADDR_LEN] =
	    { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
	uint8_t chains;
	int err;

	if (!isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_STA_TYPE)) {
		IWX_ERR(sc, "firmware lacks API_STA_TYPE, IBSS not supported\n");
		return ENOTSUP;
	}

	/* PHY context as in M2 (chanctx), RX chains for MIMO */
	chains = iwx_mimo_enabled(sc) ? 2 : 1;
	err = iwx_radio_phy_cmd(sc, chains, chains, IWX_FW_CTXT_ACTION_ADD);
	if (err) {
		IWX_ERR(sc, "could not add phy context (error %d)\n", err);
		return err;
	}
	sc->sc_flags |= IWX_FLAG_PHY_ACTIVE;
	err = iwx_radio_send_rlc(sc, chains, chains);
	if (err)
		return err;

	err = iwx_radio_mac_cmd(sc, IWX_FW_CTXT_ACTION_ADD, 0);
	if (err) {
		IWX_ERR(sc, "could not add IBSS MAC context (error %d)\n", err);
		return err;
	}
	sc->sc_flags |= IWX_FLAG_MAC_ACTIVE;
	sc->sc_rx_fcs_included =
	    !(sc->sc_radio_flags & IWX_RADIO_F_NO_PROMISC);

	err = iwx_radio_send_beacon(sc);
	if (err) {
		IWX_ERR(sc, "could not send beacon template (error %d)\n", err);
		return err;
	}

	err = iwx_radio_binding_cmd(sc, IWX_FW_CTXT_ACTION_ADD);
	if (err) {
		IWX_ERR(sc, "could not add binding (error %d)\n", err);
		return err;
	}
	sc->sc_flags |= IWX_FLAG_BINDING_ACTIVE;

	/* iwl_mvm_add_mcast_sta(): station first, then its CAB queue */
	err = iwx_radio_add_sta(sc, IWX_IBSS_MCAST_STA_ID, IWX_STA_MULTICAST,
	    mcast_addr, 0);
	if (err) {
		IWX_ERR(sc, "could not add mcast sta (error %d)\n", err);
		return err;
	}
	sc->sc_mcast_sta_active = 1;
	err = iwx_radio_enable_txq(sc, IWX_IBSS_MCAST_STA_ID,
	    IWX_IBSS_CAB_QUEUE, 0, IWX_TX_RING_COUNT);
	if (err) {
		IWX_ERR(sc, "could not enable CAB queue (error %d)\n", err);
		return err;
	}

	/* iwl_mvm_send_add_bcast_sta(): station, then the injection queue */
	err = iwx_radio_add_sta(sc, IWX_IBSS_BCAST_STA_ID,
	    IWX_STA_GENERAL_PURPOSE, bcast_addr, 0);
	if (err) {
		IWX_ERR(sc, "could not add bcast sta (error %d)\n", err);
		return err;
	}
	sc->sc_bcast_sta_active = 1;
	err = iwx_radio_enable_txq(sc, IWX_IBSS_BCAST_STA_ID,
	    IWX_IBSS_TX_QUEUE, IWX_MGMT_TID, IWX_TX_RING_COUNT);
	if (err) {
		IWX_ERR(sc, "could not enable Tx queue %d (error %d)\n",
		    IWX_IBSS_TX_QUEUE, err);
		return err;
	}
	sc->sc_tx_qid = IWX_IBSS_TX_QUEUE;
	sc->sc_tx_sta_id = IWX_IBSS_BCAST_STA_ID;

	/* iwl_mvm_sf_update(): no associated station -> INIT_OFF */
	return iwx_radio_sf_config(sc, IWX_SF_INIT_OFF);
}

/*
 * Reverse of the bring-up, safe on a partially started radio:
 * M2: iwx_run_stop() then iwx_deauth(); M3: Linux iwl_mvm_stop_ap_ibss()
 * (mcast station, then bcast station) + remove_interface.
 */
static int
iwx_radio_teardown(struct iwx_softc *sc)
{
	int err;

	sc->sc_rx_enabled = 0;

	if (sc->sc_radio_mode == IWX_RADIO_MODE_LISTENER &&
	    (sc->sc_flags & IWX_FLAG_STA_ACTIVE) &&
	    sc->sc_state == IWX_STATE_RADIO_ON) {
		/* iwx_run_stop() */
		err = iwx_radio_flush_sta(sc, IWX_MONITOR_STA_ID);
		if (err)
			return err;
		err = iwx_radio_sf_config(sc, IWX_SF_INIT_OFF);
		if (err)
			return err;
		err = iwx_disable_beacon_filter(sc);
		if (err) {
			IWX_ERR(sc, "could not disable beacon filter "
			    "(error %d)\n", err);
			return err;
		}
		err = iwx_radio_mac_cmd(sc, IWX_FW_CTXT_ACTION_MODIFY, 0);
		if (err) {
			IWX_ERR(sc, "failed to update MAC\n");
			return err;
		}
	}

	/* iwx_deauth() -> iwx_rm_sta() */
	if (sc->sc_flags & IWX_FLAG_STA_ACTIVE) {
		err = iwx_radio_flush_sta(sc, IWX_MONITOR_STA_ID);
		if (err)
			return err;
		if (sc->qenablemsk & (1 << IWX_DQA_INJECT_MONITOR_QUEUE)) {
			err = iwx_radio_disable_txq(sc, IWX_MONITOR_STA_ID,
			    IWX_DQA_INJECT_MONITOR_QUEUE, IWX_MGMT_TID);
			if (err)
				return err;
		}
		sc->sc_tx_qid = 0;
		err = iwx_radio_rm_sta_cmd(sc, IWX_MONITOR_STA_ID);
		if (err) {
			IWX_ERR(sc, "could not remove STA (error %d)\n", err);
			return err;
		}
		sc->sc_flags &= ~IWX_FLAG_STA_ACTIVE;
	}

	/* M3 stations: Linux iwl_mvm_rm_mcast_sta(), _send_rm_bcast_sta() */
	if (sc->sc_mcast_sta_active) {
		if (sc->qenablemsk & (1 << IWX_IBSS_CAB_QUEUE)) {
			err = iwx_radio_flush_sta_tids(sc,
			    IWX_IBSS_MCAST_STA_ID, 0xffff);
			if (err)
				return err;
			err = iwx_radio_disable_txq(sc, IWX_IBSS_MCAST_STA_ID,
			    IWX_IBSS_CAB_QUEUE, 0);
			if (err)
				return err;
		}
		err = iwx_radio_rm_sta_cmd(sc, IWX_IBSS_MCAST_STA_ID);
		if (err)
			return err;
		sc->sc_mcast_sta_active = 0;
	}
	if (sc->sc_bcast_sta_active) {
		if (sc->qenablemsk & (1 << IWX_IBSS_TX_QUEUE)) {
			err = iwx_radio_flush_sta_tids(sc,
			    IWX_IBSS_BCAST_STA_ID, 0xffff);
			if (err)
				return err;
			err = iwx_radio_disable_txq(sc, IWX_IBSS_BCAST_STA_ID,
			    IWX_IBSS_TX_QUEUE, IWX_MGMT_TID);
			if (err)
				return err;
		}
		sc->sc_tx_qid = 0;
		err = iwx_radio_rm_sta_cmd(sc, IWX_IBSS_BCAST_STA_ID);
		if (err)
			return err;
		sc->sc_bcast_sta_active = 0;
	}

	if (sc->sc_flags & IWX_FLAG_BINDING_ACTIVE) {
		err = iwx_radio_binding_cmd(sc, IWX_FW_CTXT_ACTION_REMOVE);
		if (err) {
			IWX_ERR(sc, "could not remove binding (error %d)\n",
			    err);
			return err;
		}
		sc->sc_flags &= ~IWX_FLAG_BINDING_ACTIVE;
	}
	if (sc->sc_flags & IWX_FLAG_MAC_ACTIVE) {
		err = iwx_radio_mac_cmd(sc, IWX_FW_CTXT_ACTION_REMOVE, 0);
		if (err) {
			IWX_ERR(sc, "could not remove MAC context (error %d)\n",
			    err);
			return err;
		}
		sc->sc_flags &= ~IWX_FLAG_MAC_ACTIVE;
	}
	if (sc->sc_flags & IWX_FLAG_PHY_ACTIVE) {
		err = iwx_radio_phy_cmd(sc, 1, 1, IWX_FW_CTXT_ACTION_REMOVE);
		if (err) {
			IWX_ERR(sc, "could not remove PHY context (error %d)\n",
			    err);
			return err;
		}
		sc->sc_flags &= ~IWX_FLAG_PHY_ACTIVE;
	}
	sc->sc_rx_fcs_included = 0;
	return 0;
}

static int
iwx_radio_channel_ok(struct iwx_softc *sc, uint8_t channel, int *active)
{
	int i;

	for (i = 0; i < sc->sc_nvm.nchan; i++) {
		if (sc->sc_nvm.channels[i].ieee != channel)
			continue;
		if (!(sc->sc_nvm.channels[i].nvm_flags & IWX_NVM_CHANNEL_VALID))
			return 0;
		*active = (sc->sc_nvm.channels[i].nvm_flags &
		    IWX_NVM_CHANNEL_ACTIVE) != 0;
		return 1;
	}
	return 0;
}

int
iwx_radio_start(struct iwx_softc *sc, const struct iwx_radio_params *p)
{
	static const uint8_t zero[ETHER_ADDR_LEN] = { 0 };
	int err, active = 0, mode;

	if (p == NULL || p->channel == 0)
		return EINVAL;
	if (sc->sc_state == IWX_STATE_RADIO_ON)
		return EBUSY;
	if (sc->sc_state != IWX_STATE_FW_READY)
		return ENXIO;
	if (!iwx_radio_channel_ok(sc, p->channel, &active))
		return EINVAL;
	if (p->flags & ~(IWX_RADIO_F_LISTENER_ONLY | IWX_RADIO_F_KEEP_BAD_FCS |
	    IWX_RADIO_F_NO_PROMISC))
		return EINVAL;
	mode = (p->flags & IWX_RADIO_F_LISTENER_ONLY) ?
	    IWX_RADIO_MODE_LISTENER : IWX_RADIO_MODE_IBSS;
	if (mode == IWX_RADIO_MODE_IBSS) {
		/* IBSS beacons: no passive (no-IR) channels, real BSSID */
		if (!active || memcmp(p->bssid, zero, ETHER_ADDR_LEN) == 0 ||
		    (p->bssid[0] & 0x01))
			return EINVAL;
	}
	if (sc->sc_use_mld_api)
		return ENOTSUP;		/* MLD firmware API not ported */

	sc->sc_radio_mode = mode;
	sc->sc_radio_flags = p->flags;
	memcpy(sc->sc_bssid, p->bssid, ETHER_ADDR_LEN);
	sc->sc_beacon_int = p->beacon_int ? p->beacon_int :
	    IWX_RADIO_DEFAULT_BINT;
	sc->sc_phyctxt[0].channel = p->channel;
	sc->sc_phyctxt[0].sco = 0;		/* IEEE80211_HTOP0_SCO_SCN */
	sc->sc_phyctxt[0].vht_chan_width = 0;	/* CHAN_WIDTH_HT */
	sc->sc_rx_keep_bad_fcs = (p->flags & IWX_RADIO_F_KEEP_BAD_FCS) != 0;
	sc->sc_ampdu_toggle = 0;
	sc->sc_ampdu_ref = 0;
	sc->sc_tx_qid = 0;

	if (mode == IWX_RADIO_MODE_LISTENER)
		err = iwx_radio_up_listener(sc);
	else
		err = iwx_radio_up_ibss(sc);
	if (err) {
		int generation = sc->sc_generation;

		/* undo what was set up (iwx_auth error path) */
		if ((sc->sc_flags & (IWX_FLAG_FW_ERR | IWX_FLAG_HW_ERR)) ||
		    iwx_radio_teardown(sc) != 0) {
			if (generation == sc->sc_generation)
				iwx_stop(sc);
			sc->sc_state = IWX_STATE_ERROR;
		}
		sc->sc_radio_mode = IWX_RADIO_MODE_OFF;
		return err;
	}

	sc->sc_tx_seq = 0;
	sc->sc_rx_enabled = 1;
	sc->sc_state = IWX_STATE_RADIO_ON;
	return 0;
}

int
iwx_radio_stop(struct iwx_softc *sc)
{
	int err;

	sc->sc_rx_enabled = 0;
	if (sc->sc_state != IWX_STATE_RADIO_ON)
		return 0;

	if (sc->sc_flags & (IWX_FLAG_FW_ERR | IWX_FLAG_HW_ERR))
		err = EIO;
	else
		err = iwx_radio_teardown(sc);
	if (err) {
		/* D-RA7: firmware state unknown, take the device down */
		IWX_ERR(sc, "radio teardown failed (error %d), "
		    "stopping device\n", err);
		iwx_stop(sc);
		return err;
	}
	sc->sc_radio_mode = IWX_RADIO_MODE_OFF;
	sc->sc_state = IWX_STATE_FW_READY;
	return 0;
}

/* ---- M3: injection ------------------------------------------------------ */

/* iwx_tx_update_byte_tbl() */
static void
iwx_tx_update_byte_tbl(struct iwx_softc *sc, struct iwx_tx_ring *txq,
    int idx, uint16_t byte_cnt, uint16_t num_tbs)
{
	uint8_t filled_tfd_size, num_fetch_chunks;
	uint16_t len = byte_cnt;
	uint16_t bc_ent;

	filled_tfd_size = (uint8_t)(offsetof(struct iwx_tfh_tfd, tbs) +
	    num_tbs * sizeof(struct iwx_tfh_tb));
	/*
	 * filled_tfd_size contains the number of filled bytes in the TFD.
	 * Dividing it by 64 will give the number of chunks to fetch
	 * to SRAM- 0 for one chunk, 1 for 2 and so on.
	 * If, for example, TFD contains only 3 TBs then 32 bytes
	 * of the TFD are used, and only one chunk of 64 bytes should
	 * be fetched
	 */
	num_fetch_chunks = (uint8_t)(howmany(filled_tfd_size, 64) - 1);

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		struct iwx_gen3_bc_tbl_entry *scd_bc_tbl = txq->bc_tbl.vaddr;
		/* Starting from AX210, the HW expects bytes */
		bc_ent = htole16((uint16_t)(len | (num_fetch_chunks << 14)));
		scd_bc_tbl[idx].tfd_offset = bc_ent;
	} else {
		struct iwx_agn_scd_bc_tbl *scd_bc_tbl = txq->bc_tbl.vaddr;
		/* Before AX210, the HW expects DW */
		len = (uint16_t)howmany(len, 4);
		bc_ent = htole16((uint16_t)(len | (num_fetch_chunks << 12)));
		scd_bc_tbl->tfd_offset[idx] = bc_ent;
	}
}

static void
iwx_tx_report(struct iwx_softc *sc, struct iwx_tx_data *data,
    uint32_t result, const struct iwx_tx_resp *resp)
{
	struct iwx_tx_status st;

	memset(&st, 0, sizeof(st));
	st.cookie = data->cookie;
	st.result = result;
	st.flags = data->txr_flags;
	st.seq_ctl = data->seq_ctl;
	if (resp != NULL) {
		st.fw_status = le16toh(resp->status.status);
		st.retries = resp->failure_frame;
		st.rts_retries = resp->failure_rts;
		st.rate_n_flags = le32toh(resp->initial_rate);
	}
	data->inflight = 0;
	switch (result) {
	case IWX_TXST_SUCCESS:
		sc->sc_stat_tx_success++;
		break;
	case IWX_TXST_FAILED:
		sc->sc_stat_tx_failed++;
		break;
	default:
		sc->sc_stat_tx_flushed++;
		break;
	}
	if (st.cookie != IWX_TX_COOKIE_NONE)
		osdep_tx_status(sc->sc_os, &st);
}

/* Called with osdep_lock held. */
void
iwx_txq_flush_report(struct iwx_softc *sc, struct iwx_tx_ring *ring)
{
	int i;

	for (i = 0; i < IWX_TX_RING_COUNT; i++) {
		if (ring->data[i].inflight)
			iwx_tx_report(sc, &ring->data[i], IWX_TXST_FLUSHED,
			    NULL);
	}
}

/*
 * iwx_txq_advance(): free TFDs up to (not including) ring index 'idx'.
 * Frames without their own TX_CMD response get 'result'. osdep_lock held.
 */
static void
iwx_txq_advance(struct iwx_softc *sc, struct iwx_tx_ring *ring,
    uint16_t idx, uint32_t result)
{
	struct iwx_tx_data *txd;
	uint32_t dist, mask = (uint32_t)sc->max_tfd_queue_size - 1;

	/* airdrop: ignore an SSN outside the queued window */
	dist = ((uint32_t)idx - (uint32_t)ring->tail_hw) & mask;
	if (dist > (uint32_t)ring->queued)
		return;

	while (ring->tail_hw != idx) {
		txd = &ring->data[ring->tail];
		if (txd->flags & IWX_TXDATA_FLAG_TX_QUEUED) {
			memset(&ring->desc[ring->tail], 0,
			    sizeof(ring->desc[ring->tail]));
			iwx_tx_update_byte_tbl(sc, ring, ring->tail, 0, 0);
			if (txd->inflight)
				iwx_tx_report(sc, txd, result, NULL);
			txd->flags &= ~IWX_TXDATA_FLAG_TX_QUEUED;
			ring->queued--;
		}
		ring->tail = (ring->tail + 1) % IWX_TX_RING_COUNT;
		ring->tail_hw = (ring->tail_hw + 1) % sc->max_tfd_queue_size;
	}
}

/* iwx_rx_tx_cmd(): DPC, osdep_lock held */
void
iwx_rx_tx_cmd(struct iwx_softc *sc, const struct iwx_rx_packet *pkt)
{
	int qid = pkt->hdr.qid, idx = pkt->hdr.idx;
	const struct iwx_tx_resp *tx_resp = (const void *)pkt->data;
	struct iwx_tx_ring *ring;
	uint32_t len = iwx_rx_packet_payload_len(pkt);
	uint32_t ssn, status;

	if (qid <= IWX_DQA_CMD_QUEUE || qid >= IWX_NUM_TX_QUEUES)
		return;
	ring = &sc->txq[qid];
	if (!ring->allocated)
		return;

	/* Sanity checks. */
	if (sizeof(*tx_resp) > len)
		return;
	if (tx_resp->frame_count != 1)
		return;		/* A-MPDU: never queued by this driver */
	if (sizeof(*tx_resp) + sizeof(ssn) > len)
		return;

	status = le16toh(tx_resp->status.status) & IWX_TX_STATUS_MSK;
	if (idx < IWX_TX_RING_COUNT && ring->data[idx].inflight) {
		iwx_tx_report(sc, &ring->data[idx],
		    (status == IWX_TX_STATUS_SUCCESS ||
		    status == IWX_TX_STATUS_DIRECT_DONE) ?
		    IWX_TXST_SUCCESS : IWX_TXST_FAILED, tx_resp);
	}

	/*
	 * On hardware supported by iwx(4) the SSN counter corresponds
	 * to a Tx ring index rather than a sequence number.
	 * Frames up to this index (non-inclusive) can now be freed.
	 */
	memcpy(&ssn, (const uint8_t *)&tx_resp->status +
	    tx_resp->frame_count * sizeof(tx_resp->status), sizeof(ssn));
	ssn = le32toh(ssn);
	if (ssn < (uint32_t)sc->max_tfd_queue_size)
		iwx_txq_advance(sc, ring, (uint16_t)ssn, IWX_TXST_NO_STATUS);
}

/*
 * rate_n_flags for an injected frame (D-TX1).
 * Legacy: iwx_tx_fill_cmd() for a forced CCK/OFDM rate, antenna A.
 * HT: Linux iwl_mvm_get_inject_tx_rate() (RATE_MCS_HT_MSK | code | NSS).
 */
int
iwx_tx_rate_n_flags(struct iwx_softc *sc, const struct iwx_tx_params *p,
    uint32_t *rnf)
{
	static const struct { uint8_t rate, plcp, cck, idx; } legacy[] = {
		{ 2, 10, 1, 0 }, { 4, 20, 1, 1 }, { 11, 55, 1, 2 },
		{ 22, 110, 1, 3 },
		{ 12, 0xd, 0, 0 }, { 18, 0xf, 0, 1 }, { 24, 0x5, 0, 2 },
		{ 36, 0x7, 0, 3 }, { 48, 0x9, 0, 4 }, { 72, 0xb, 0, 5 },
		{ 96, 0x1, 0, 6 }, { 108, 0x3, 0, 7 },
	};
	uint32_t r;
	size_t i;

	if (p->flags & IWX_TX_F_RATE_HT) {
		uint32_t mcs = p->rate;

		if (mcs > 15)
			return EINVAL;
		if (mcs >= 8 && (!iwx_mimo_enabled(sc) ||
		    (iwx_fw_valid_tx_ant(sc) & IWX_ANT_AB) != IWX_ANT_AB))
			return EINVAL;
		r = (mcs >= 8) ? IWX_RATE_MCS_ANT_AB_MSK :
		    IWX_RATE_MCS_ANT_A_MSK;
		if (sc->sc_rate_n_flags_version >= 2) {
			r |= IWX_RATE_MCS_HT_MSK | (mcs & IWX_RATE_HT_MCS_CODE_MSK) |
			    ((mcs >> 3) << IWX_RATE_MCS_NSS_POS);
			if (p->flags & IWX_TX_F_SGI)
				r |= IWX_RATE_MCS_SGI_MSK;
		} else {
			r |= IWX_RATE_MCS_HT_MSK_V1 | mcs;
			if (p->flags & IWX_TX_F_SGI)
				r |= IWX_RATE_MCS_SGI_MSK_V1;
		}
		*rnf = r;
		return 0;
	}
	if (p->flags & IWX_TX_F_SGI)
		return EINVAL;
	for (i = 0; i < nitems(legacy); i++) {
		if (legacy[i].rate != p->rate)
			continue;
		if (legacy[i].cck && !iwx_radio_is_2ghz(sc->sc_phyctxt[0].channel))
			return EINVAL;
		r = IWX_RATE_MCS_ANT_A_MSK;
		if (sc->sc_rate_n_flags_version >= 2)
			r |= (legacy[i].cck ? IWX_RATE_MCS_CCK_MSK :
			    IWX_RATE_MCS_LEGACY_OFDM_MSK) |
			    (legacy[i].idx & IWX_RATE_LEGACY_RATE_MSK);
		else
			r |= (legacy[i].cck ? IWX_RATE_MCS_CCK_MSK_V1 : 0) |
			    legacy[i].plcp;
		*rnf = r;
		return 0;
	}
	return EINVAL;
}

int
iwx_tx_inject(struct iwx_softc *sc, const uint8_t *frame, uint32_t len,
    const struct iwx_tx_params *p, uint32_t *txr_flags)
{
	struct iwx_tx_ring *ring;
	struct iwx_tx_data *data;
	struct iwx_tfh_tfd *desc;
	struct iwx_device_cmd *cmd;
	struct iwx_tx_cmd_gen3 *tx;
	uint8_t *hdr, *pay;
	uint64_t paddr;
	uint32_t hdrlen, pad, paylen, rnf, offload_assist, flags = 0;
	uint16_t num_tbs, txflags;
	uint8_t type, subtype;
	size_t txcmd_size;
	int idx, err;

	if (txr_flags != NULL)
		*txr_flags = 0;
	if (sc->sc_state != IWX_STATE_RADIO_ON || sc->sc_tx_qid == 0)
		return ENXIO;
	if (sc->sc_flags & (IWX_FLAG_FW_ERR | IWX_FLAG_HW_ERR))
		return EIO;
	if (p == NULL || frame == NULL || len < IEEE80211_MIN_FRAME_LEN ||
	    len > IWX_TX_MAX_FRAME)
		return EINVAL;
	if (sc->sc_device_family < IWX_DEVICE_FAMILY_AX210)
		return ENOTSUP;		/* gen2 TX command not wired up */
	if ((frame[0] & IEEE80211_FC0_VERSION_MASK) != 0)
		return EINVAL;
	type = frame[0] & IEEE80211_FC0_TYPE_MASK;
	subtype = frame[0] & IEEE80211_FC0_SUBTYPE_MASK;
	if (type == 0x0c)	/* reserved / extension frame type */
		return EINVAL;
	hdrlen = iwx_80211_hdrlen_full(frame);
	if (hdrlen > len)
		return EINVAL;
	err = iwx_tx_rate_n_flags(sc, p, &rnf);
	if (err)
		return err;

	ring = &sc->txq[sc->sc_tx_qid];
	if (!ring->allocated || ring->pay_dma.vaddr == NULL)
		return ENXIO;

	paylen = len - hdrlen;
	/* iwx_tx(): first segment length must be a multiple of 4 */
	offload_assist = IWX_TX_CMD_OFFLD_MH_SIZE((hdrlen / 2) &
	    IWX_TX_CMD_OFFLD_MH_MASK);
	if (hdrlen & 3) {
		pad = 4 - (hdrlen & 3);
		offload_assist |= IWX_TX_CMD_OFFLD_PAD;
	} else
		pad = 0;
	txflags = IWX_TX_FLAGS_CMD_RATE | IWX_TX_FLAGS_ENCRYPT_DIS;
	if (p->flags & IWX_TX_F_HIGH_PRI)
		txflags |= IWX_TX_FLAGS_HIGH_PRI;
	txcmd_size = sizeof(*tx);

	osdep_lock(sc->sc_os);
	if (ring->queued >= IWX_TX_MAX_INFLIGHT) {
		osdep_unlock(sc->sc_os);
		return ENOBUFS;
	}
	idx = ring->cur;
	desc = &ring->desc[idx];
	memset(desc, 0, sizeof(*desc));
	data = &ring->data[idx];
	cmd = &ring->cmd[idx];
	memset(cmd, 0, sizeof(cmd->hdr) + txcmd_size + hdrlen + pad);
	cmd->hdr.code = IWX_TX_CMD;
	cmd->hdr.flags = 0;
	cmd->hdr.qid = (uint8_t)ring->qid;
	cmd->hdr.idx = (uint8_t)idx;

	tx = (void *)cmd->data;
	tx->len = htole16((uint16_t)len);
	tx->offload_assist = htole32(offload_assist);
	tx->flags = htole16(txflags);
	tx->rate_n_flags = htole32(rnf);
	hdr = (uint8_t *)cmd->data + txcmd_size;
	memcpy(hdr, frame, hdrlen);

	/* D-TX2: no-ACK request */
	if ((p->flags & IWX_TX_F_NOACK) && type != IEEE80211_FC0_TYPE_CTL &&
	    !(hdr[4] & 0x01)) {
		if (type == IEEE80211_FC0_TYPE_DATA && (subtype & 0x80)) {
			uint32_t qos = ((hdr[1] & 0x03) == 0x03) ? 30 : 24;
			/* QoS Control bits 5-6: Ack Policy 01 = No Ack */
			hdr[qos] = (uint8_t)((hdr[qos] & ~0x60) | 0x20);
			flags |= IWX_TXR_F_ACK_POLICY_SET;
		} else
			flags |= IWX_TXR_F_NOACK_NOT_HONORED;
	}
	if (type != IEEE80211_FC0_TYPE_CTL) {
		if (p->flags & IWX_TX_F_ASSIGN_SEQ) {
			uint16_t sc_ = (uint16_t)(((sc->sc_tx_seq & 0xfff) << 4) |
			    (hdr[22] & 0x0f));
			hdr[22] = (uint8_t)(sc_ & 0xff);
			hdr[23] = (uint8_t)(sc_ >> 8);
			sc->sc_tx_seq = (uint16_t)((sc->sc_tx_seq + 1) & 0xfff);
			flags |= IWX_TXR_F_SEQ_ASSIGNED;
		}
		data->seq_ctl = (uint16_t)(hdr[22] | (hdr[23] << 8));
	} else
		data->seq_ctl = 0;

	pay = (uint8_t *)ring->pay_dma.vaddr +
	    (size_t)(idx % IWX_TX_MAX_INFLIGHT) * IWX_TX_PAYLOAD_SLOT;
	if (paylen)
		memcpy(pay, frame + hdrlen, paylen);

	/* Fill TX descriptor. */
	num_tbs = (uint16_t)(paylen ? 3 : 2);
	desc->num_tbs = htole16(num_tbs);
	desc->tbs[0].tb_len = htole16(IWX_FIRST_TB_SIZE);
	paddr = htole64(data->cmd_paddr);
	memcpy(&desc->tbs[0].addr, &paddr, sizeof(paddr));
	desc->tbs[1].tb_len = htole16((uint16_t)(sizeof(struct iwx_cmd_header) +
	    txcmd_size + hdrlen + pad - IWX_FIRST_TB_SIZE));
	paddr = htole64(data->cmd_paddr + IWX_FIRST_TB_SIZE);
	memcpy(&desc->tbs[1].addr, &paddr, sizeof(paddr));
	if (paylen) {
		desc->tbs[2].tb_len = htole16((uint16_t)paylen);
		paddr = htole64(ring->pay_dma.paddr +
		    (uint64_t)(idx % IWX_TX_MAX_INFLIGHT) * IWX_TX_PAYLOAD_SLOT);
		memcpy(&desc->tbs[2].addr, &paddr, sizeof(paddr));
	}

	data->flags |= IWX_TXDATA_FLAG_TX_QUEUED;
	data->inflight = 1;
	data->cookie = p->cookie;
	data->txr_flags = flags;

	iwx_tx_update_byte_tbl(sc, ring, idx, (uint16_t)len, num_tbs);
	IWX_BARRIER_WRITE(sc);

	/* Kick TX ring. */
	ring->cur = (ring->cur + 1) % IWX_TX_RING_COUNT;
	ring->cur_hw = (ring->cur_hw + 1) % sc->max_tfd_queue_size;
	ring->queued++;
	sc->sc_stat_tx_submitted++;
	IWX_WRITE(sc, IWX_HBUS_TARG_WRPTR, ring->qid << 16 | ring->cur_hw);
	osdep_unlock(sc->sc_os);

	if (txr_flags != NULL)
		*txr_flags = flags;
	return 0;
}
