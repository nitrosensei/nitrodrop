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
 * Minimal IEEE 802.11 definitions needed by iwx_reg.h and the core.
 * The driver has no net80211 stack: frames are passed raw to user mode.
 * Field names follow the BSD net80211 convention (written from the
 * 802.11 standard, not copied from any net80211 implementation).
 */
#ifndef AIRDROP_IEEE80211_H
#define AIRDROP_IEEE80211_H

#include "osdep.h"

#pragma pack(push, 1)
struct ieee80211_frame {
	uint8_t		i_fc[2];
	uint8_t		i_dur[2];
	uint8_t		i_addr1[ETHER_ADDR_LEN];
	uint8_t		i_addr2[ETHER_ADDR_LEN];
	uint8_t		i_addr3[ETHER_ADDR_LEN];
	uint8_t		i_seq[2];
};

/* ACK / CTS: frame control, duration, receiver address */
struct ieee80211_frame_ack {
	uint8_t		i_fc[2];
	uint8_t		i_dur[2];
	uint8_t		i_ra[ETHER_ADDR_LEN];
};
#pragma pack(pop)

#define IEEE80211_FC0_VERSION_MASK	0x03
#define IEEE80211_FC0_TYPE_MASK		0x0c
#define IEEE80211_FC0_TYPE_MGT		0x00
#define IEEE80211_FC0_TYPE_CTL		0x04
#define IEEE80211_FC0_TYPE_DATA		0x08
#define IEEE80211_FC0_SUBTYPE_MASK	0xf0

/* Smallest frame we pass up: ACK/CTS (10 bytes). */
#define IEEE80211_MIN_FRAME_LEN		((uint32_t)sizeof(struct ieee80211_frame_ack))

#endif /* AIRDROP_IEEE80211_H */
