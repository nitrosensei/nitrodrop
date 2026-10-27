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
 * airdrop-win port notes (driver/core/iwx_cmd.c)
 *
 * Host command queue and RX packet dispatch, ported from OpenBSD if_iwx.c
 * rev 1.230: iwx_send_cmd(), iwx_send_cmd_pdu(), iwx_send_cmd_status(),
 * iwx_send_cmd_pdu_status(), iwx_free_resp(), iwx_cmd_done(),
 * iwx_rx_pkt_valid(), iwx_rx_pkt().
 *
 * Divergences from iwx (all intentional):
 *   D-CMD1 tsleep/wakeup -> iwx_wait_cond(): an OS event per wait channel
 *          (ALIVE, INIT, CMD) plus a condition re-checked in a loop, so a
 *          lost or spurious wakeup cannot break it. A waiter also gives up
 *          early (EIO) once a fatal firmware/hardware error was flagged;
 *          iwx just sleeps until its timeout.
 *   D-CMD2 per-slot completion flag (iwx_tx_data.done) instead of the
 *          sleep channel &ring->desc[idx].
 *   D-CMD3 commands larger than IWX_DEF_CMD_PAYLOAD_SIZE go through one
 *          pre-allocated 4 KiB DMA buffer (sc->cmd_big_dma) instead of a
 *          freshly allocated mbuf cluster: allocating DMA memory per
 *          command is not possible at DISPATCH_LEVEL with KMDF common
 *          buffers. Only one such command can be in flight (EBUSY).
 *   D-CMD4 command responses are stored for any non-notification packet
 *          on the command queue (Linux iwlwifi rule: !(qid & 0x80)),
 *          instead of iwx's explicit list of command codes. Every reply
 *          completes its command; iwx only completes listed codes and
 *          lets the rest time out after 1 s (e.g. new firmware replies).
 *   D-CMD5 qid/idx from the device are bounds-checked before indexing
 *          driver arrays (iwx indexes sc->txq[qid] unchecked).
 *   D-CMD6 net80211 consumers (scan, BA, rate control, beacon miss,
 *          statistics) are not ported; their notifications are counted as
 *          handled-and-ignored. RX MPDUs go to iwx_rx_mpdu_mq() which
 *          copies the frame to the OS backend (no mbuf swap, D-TR1).
 *   D-CMD7 locking: ring state shared with the DPC is protected with
 *          osdep_lock() (iwx: splnet()).
 */

#include "iwx_var.h"

/*
 * Wait until (*cond & mask) == mask. Returns 0, ETIMEDOUT, or EIO if a
 * fatal error was flagged meanwhile (D-CMD1).
 */
int
iwx_wait_cond(struct iwx_softc *sc, enum osdep_event_id ev,
    const volatile int *cond, int mask, uint32_t timeout_ms)
{
	uint64_t deadline = osdep_time_us() + (uint64_t)timeout_ms * 1000;

	for (;;) {
		uint64_t now;

		if ((*cond & mask) == mask)
			return 0;
		osdep_event_clear(sc->sc_os, ev);
		/* re-check after clearing: the signal may have just fired */
		if ((*cond & mask) == mask)
			return 0;
		if (sc->sc_flags & (IWX_FLAG_FW_ERR | IWX_FLAG_HW_ERR))
			return EIO;
		now = osdep_time_us();
		if (now >= deadline)
			return ETIMEDOUT;
		(void)osdep_event_wait(sc->sc_os, ev,
		    (uint32_t)((deadline - now + 999) / 1000));
	}
}

int
iwx_send_cmd(struct iwx_softc *sc, struct iwx_host_cmd *hcmd)
{
	struct iwx_tx_ring *ring = &sc->txq[IWX_DQA_CMD_QUEUE];
	struct iwx_tfh_tfd *desc;
	struct iwx_tx_data *txdata;
	struct iwx_device_cmd *cmd;
	uint64_t paddr;
	uint64_t addr;
	int err = 0, i, off;
	size_t paylen;
	int idx, async, group_id, big = 0;
	uint32_t code;
	size_t hdrlen, datasz;
	uint8_t *data;
	uint8_t *resp_buf = NULL;
	int generation = sc->sc_generation;

	if (!ring->allocated)
		return ENXIO;

	code = hcmd->id;
	async = hcmd->flags & IWX_CMD_ASYNC;

	for (i = 0, paylen = 0; i < (int)nitems(hcmd->len); i++) {
		paylen += hcmd->len[i];
	}

	/* If this command waits for a response, allocate response buffer. */
	hcmd->resp_pkt = NULL;
	if (hcmd->flags & IWX_CMD_WANT_RESP) {
		if (async ||
		    hcmd->resp_pkt_len < sizeof(struct iwx_rx_packet) ||
		    hcmd->resp_pkt_len > IWX_CMD_RESP_MAX) {
			IWX_ERR(sc, "BUG: bad response request for cmd 0x%x\n",
			    code);
			return EINVAL;
		}
		resp_buf = osdep_malloc(hcmd->resp_pkt_len);
		if (resp_buf == NULL)
			return ENOMEM;
		memset(resp_buf, 0, hcmd->resp_pkt_len);
	}

	osdep_lock(sc->sc_os);

	idx = ring->cur;
	desc = &ring->desc[idx];
	txdata = &ring->data[idx];

	if (ring->queued >= IWX_TX_RING_COUNT - 1) {
		err = ENOSPC;
		goto out;
	}

	if (resp_buf != NULL) {
		if (sc->sc_cmd_resp_pkt[idx] != NULL) {
			err = ENOSPC;
			goto out;
		}
		sc->sc_cmd_resp_pkt[idx] = resp_buf;
		sc->sc_cmd_resp_len[idx] = hcmd->resp_pkt_len;
		resp_buf = NULL;	/* now owned by the ring slot */
	} else {
		sc->sc_cmd_resp_pkt[idx] = NULL;
	}

	/*
	 * XXX Intel inside (tm)
	 * Firmware API versions >= 50 reject old-style commands in
	 * group 0 with a "BAD_COMMAND" firmware error. We must pretend
	 * that such commands were in the LONG_GROUP instead in order
	 * for firmware to accept them.
	 */
	txdata->flags = 0;
	if (iwx_cmd_groupid(code) == 0) {
		code = IWX_WIDE_ID(IWX_LONG_GROUP, code);
		txdata->flags |= IWX_TXDATA_FLAG_CMD_IS_NARROW;
	}

	group_id = iwx_cmd_groupid(code);

	hdrlen = sizeof(cmd->hdr_wide);
	datasz = sizeof(cmd->data_wide);

	if (paylen > datasz) {
		/* Command is too large to fit in pre-allocated space. */
		size_t totlen = hdrlen + paylen;
		if (paylen > IWX_MAX_CMD_PAYLOAD_SIZE ||
		    totlen > sc->cmd_big_dma.size) {
			IWX_ERR(sc, "firmware command too long (%u bytes)\n",
			    (unsigned)totlen);
			err = EINVAL;
			goto out_resp;
		}
		if (sc->cmd_big_idx != -1) {	/* D-CMD3 */
			err = EBUSY;
			goto out_resp;
		}
		sc->cmd_big_idx = idx;
		txdata->flags |= IWX_TXDATA_FLAG_BIG_CMD;
		cmd = sc->cmd_big_dma.vaddr;
		paddr = sc->cmd_big_dma.paddr;
		big = 1;
	} else {
		cmd = &ring->cmd[idx];
		paddr = txdata->cmd_paddr;
	}

	memset(cmd, 0, big ? sc->cmd_big_dma.size : sizeof(*cmd));
	cmd->hdr_wide.opcode = iwx_cmd_opcode(code);
	cmd->hdr_wide.group_id = (uint8_t)group_id;
	cmd->hdr_wide.qid = (uint8_t)ring->qid;
	cmd->hdr_wide.idx = (uint8_t)idx;
	cmd->hdr_wide.length = htole16((uint16_t)paylen);
	cmd->hdr_wide.version = iwx_cmd_version(code);
	data = cmd->data_wide;

	for (i = 0, off = 0; i < (int)nitems(hcmd->data); i++) {
		if (hcmd->len[i] == 0)
			continue;
		memcpy(data + off, hcmd->data[i], hcmd->len[i]);
		off += hcmd->len[i];
	}

	memset(desc, 0, sizeof(*desc));
	desc->tbs[0].tb_len = htole16((uint16_t)MIN(hdrlen + paylen,
	    IWX_FIRST_TB_SIZE));
	addr = htole64(paddr);
	memcpy(&desc->tbs[0].addr, &addr, sizeof(addr));
	if (hdrlen + paylen > IWX_FIRST_TB_SIZE) {
		desc->tbs[1].tb_len = htole16((uint16_t)(hdrlen + paylen -
		    IWX_FIRST_TB_SIZE));
		addr = htole64(paddr + IWX_FIRST_TB_SIZE);
		memcpy(&desc->tbs[1].addr, &addr, sizeof(addr));
		desc->num_tbs = htole16(2);
	} else
		desc->num_tbs = htole16(1);

	txdata->done = 0;
	osdep_barrier(sc->sc_os);	/* descriptor + command before doorbell */

	/* Kick command ring. */
	DPRINTF(sc, "sending command 0x%x idx %d\n", code, idx);
	ring->queued++;
	ring->cur = (ring->cur + 1) % IWX_TX_RING_COUNT;
	ring->cur_hw = (ring->cur_hw + 1) % sc->max_tfd_queue_size;
	IWX_WRITE(sc, IWX_HBUS_TARG_WRPTR, ring->qid << 16 | ring->cur_hw);

	osdep_unlock(sc->sc_os);

	if (!async) {
		err = iwx_wait_cond(sc, OSDEP_EV_CMD, &txdata->done, 1, 1000);
		osdep_lock(sc->sc_os);
		if (err == 0) {
			/* if hardware is no longer up, return error */
			if (generation != sc->sc_generation) {
				err = ENXIO;
			} else {
				/* Freed in iwx_free_resp(). */
				hcmd->resp_pkt =
				    (void *)sc->sc_cmd_resp_pkt[idx];
				sc->sc_cmd_resp_pkt[idx] = NULL;
			}
		} else if (generation == sc->sc_generation) {
			IWX_ERR(sc, "command 0x%x idx %d failed: %d\n",
			    code, idx, err);
			if (sc->sc_cmd_resp_pkt[idx] != NULL) {
				osdep_free(sc->sc_cmd_resp_pkt[idx],
				    sc->sc_cmd_resp_len[idx]);
				sc->sc_cmd_resp_pkt[idx] = NULL;
			}
		}
		osdep_unlock(sc->sc_os);
	}
	return err;

 out_resp:
	if (sc->sc_cmd_resp_pkt[idx] != NULL) {
		osdep_free(sc->sc_cmd_resp_pkt[idx], sc->sc_cmd_resp_len[idx]);
		sc->sc_cmd_resp_pkt[idx] = NULL;
	}
 out:
	osdep_unlock(sc->sc_os);
	if (resp_buf != NULL)
		osdep_free(resp_buf, hcmd->resp_pkt_len);
	return err;
}

int
iwx_send_cmd_pdu(struct iwx_softc *sc, uint32_t id, uint32_t flags,
    uint16_t len, const void *data)
{
	struct iwx_host_cmd cmd;

	memset(&cmd, 0, sizeof(cmd));
	cmd.id = id;
	cmd.len[0] = len;
	cmd.data[0] = data;
	cmd.flags = flags;

	return iwx_send_cmd(sc, &cmd);
}

int
iwx_send_cmd_status(struct iwx_softc *sc, struct iwx_host_cmd *cmd,
    uint32_t *status)
{
	struct iwx_rx_packet *pkt;
	struct iwx_cmd_response *resp;
	int err, resp_len;

	if (cmd->flags & IWX_CMD_WANT_RESP)
		return EINVAL;	/* iwx: KASSERT */
	cmd->flags |= IWX_CMD_WANT_RESP;
	cmd->resp_pkt_len = sizeof(*pkt) + sizeof(*resp);

	err = iwx_send_cmd(sc, cmd);
	if (err)
		return err;

	pkt = cmd->resp_pkt;
	if (pkt == NULL || (pkt->hdr.flags & IWX_CMD_FAILED_MSK)) {
		if (pkt != NULL)
			iwx_free_resp(sc, cmd);	/* iwx leaks it here */
		return EIO;
	}

	resp_len = iwx_rx_packet_payload_len(pkt);
	if (resp_len != sizeof(*resp)) {
		iwx_free_resp(sc, cmd);
		return EIO;
	}

	resp = (void *)pkt->data;
	*status = le32toh(resp->status);
	iwx_free_resp(sc, cmd);
	return err;
}

int
iwx_send_cmd_pdu_status(struct iwx_softc *sc, uint32_t id, uint16_t len,
    const void *data, uint32_t *status)
{
	struct iwx_host_cmd cmd;

	memset(&cmd, 0, sizeof(cmd));
	cmd.id = id;
	cmd.len[0] = len;
	cmd.data[0] = data;

	return iwx_send_cmd_status(sc, &cmd, status);
}

void
iwx_free_resp(struct iwx_softc *sc, struct iwx_host_cmd *hcmd)
{
	(void)sc;
	if (hcmd->resp_pkt != NULL)
		osdep_free(hcmd->resp_pkt, hcmd->resp_pkt_len);
	hcmd->resp_pkt = NULL;
}

/* Called with osdep_lock held (from the DPC). */
void
iwx_cmd_done(struct iwx_softc *sc, int qid, int idx, int code)
{
	struct iwx_tx_ring *ring = &sc->txq[IWX_DQA_CMD_QUEUE];
	struct iwx_tx_data *data;

	if (qid != IWX_DQA_CMD_QUEUE) {
		return;	/* Not a command ack. */
	}
	if (idx < 0 || idx >= IWX_TX_RING_COUNT)
		return;

	data = &ring->data[idx];

	if (data->flags & IWX_TXDATA_FLAG_BIG_CMD) {
		data->flags &= ~IWX_TXDATA_FLAG_BIG_CMD;
		if (sc->cmd_big_idx == idx)
			sc->cmd_big_idx = -1;
	}
	data->done = 1;
	osdep_event_signal(sc->sc_os, OSDEP_EV_CMD);

	DPRINTF(sc, "command 0x%x done\n", code);
	if (ring->queued == 0) {
		DPRINTF(sc, "unexpected firmware response to command 0x%x\n",
		    code);
	} else if (ring->queued > 0)
		ring->queued--;
}

int
iwx_rx_pkt_valid(const struct iwx_rx_packet *pkt)
{
	int qid, idx;
	uint32_t code;

	qid = pkt->hdr.qid & ~0x80;
	idx = pkt->hdr.idx;
	code = IWX_WIDE_ID(pkt->hdr.flags, pkt->hdr.code);

	return (!(qid == 0 && idx == 0 && code == 0) &&
	    pkt->len_n_flags != htole32(IWX_FH_RSCSR_FRAME_INVALID));
}

/* D-CMD4: copy a command response into the waiting slot's buffer. */
static void
iwx_cmd_store_resp(struct iwx_softc *sc, const struct iwx_rx_packet *pkt,
    int idx)
{
	size_t pkt_len;

	if (idx < 0 || idx >= IWX_TX_RING_COUNT ||
	    sc->sc_cmd_resp_pkt[idx] == NULL)
		return;

	pkt_len = sizeof(pkt->len_n_flags) + iwx_rx_packet_len(pkt);

	if ((pkt->hdr.flags & IWX_CMD_FAILED_MSK) ||
	    pkt_len < sizeof(*pkt) ||
	    pkt_len > sc->sc_cmd_resp_len[idx]) {
		osdep_free(sc->sc_cmd_resp_pkt[idx], sc->sc_cmd_resp_len[idx]);
		sc->sc_cmd_resp_pkt[idx] = NULL;
		return;
	}

	memcpy(sc->sc_cmd_resp_pkt[idx], pkt, pkt_len);
}

static void
iwx_rx_alive(struct iwx_softc *sc, const struct iwx_rx_packet *pkt)
{
	const struct iwx_alive_resp_v4 *resp4;
	const struct iwx_alive_resp_v5 *resp5;
	const struct iwx_alive_resp_v6 *resp6;
	int notif_ver;

	notif_ver = iwx_lookup_notif_ver(sc, IWX_LEGACY_GROUP, IWX_ALIVE);
	DPRINTF(sc, "firmware alive version %d\n", notif_ver);
	sc->sc_uc.uc_ok = 0;

	/*
	 * For v5 and above, we can check the version, for older
	 * versions we need to check the size.
	 */
	if (notif_ver == 6 || notif_ver == 7) {
		resp6 = (const void *)pkt->data;
		if (iwx_rx_packet_payload_len(pkt) != sizeof(*resp6))
			goto done;
		sc->sc_uc.uc_lmac_error_event_table[0] = le32toh(
		    resp6->lmac_data[0].dbg_ptrs.error_event_table_ptr);
		sc->sc_uc.uc_lmac_error_event_table[1] = le32toh(
		    resp6->lmac_data[1].dbg_ptrs.error_event_table_ptr);
		sc->sc_uc.uc_log_event_table = le32toh(
		    resp6->lmac_data[0].dbg_ptrs.log_event_table_ptr);
		sc->sc_uc.uc_umac_error_event_table = le32toh(
		    resp6->umac_data.dbg_ptrs.error_info_addr);
		sc->sc_sku_id[0] = le32toh(resp6->sku_id.data[0]);
		sc->sc_sku_id[1] = le32toh(resp6->sku_id.data[1]);
		sc->sc_sku_id[2] = le32toh(resp6->sku_id.data[2]);
		if (resp6->status == IWX_ALIVE_STATUS_OK)
			sc->sc_uc.uc_ok = 1;
	} else if (notif_ver == 5) {
		resp5 = (const void *)pkt->data;
		if (iwx_rx_packet_payload_len(pkt) != sizeof(*resp5))
			goto done;
		sc->sc_uc.uc_lmac_error_event_table[0] = le32toh(
		    resp5->lmac_data[0].dbg_ptrs.error_event_table_ptr);
		sc->sc_uc.uc_lmac_error_event_table[1] = le32toh(
		    resp5->lmac_data[1].dbg_ptrs.error_event_table_ptr);
		sc->sc_uc.uc_log_event_table = le32toh(
		    resp5->lmac_data[0].dbg_ptrs.log_event_table_ptr);
		sc->sc_uc.uc_umac_error_event_table = le32toh(
		    resp5->umac_data.dbg_ptrs.error_info_addr);
		sc->sc_sku_id[0] = le32toh(resp5->sku_id.data[0]);
		sc->sc_sku_id[1] = le32toh(resp5->sku_id.data[1]);
		sc->sc_sku_id[2] = le32toh(resp5->sku_id.data[2]);
		if (resp5->status == IWX_ALIVE_STATUS_OK)
			sc->sc_uc.uc_ok = 1;
	} else if (iwx_rx_packet_payload_len(pkt) == sizeof(*resp4)) {
		resp4 = (const void *)pkt->data;
		sc->sc_uc.uc_lmac_error_event_table[0] = le32toh(
		    resp4->lmac_data[0].dbg_ptrs.error_event_table_ptr);
		sc->sc_uc.uc_lmac_error_event_table[1] = le32toh(
		    resp4->lmac_data[1].dbg_ptrs.error_event_table_ptr);
		sc->sc_uc.uc_log_event_table = le32toh(
		    resp4->lmac_data[0].dbg_ptrs.log_event_table_ptr);
		sc->sc_uc.uc_umac_error_event_table = le32toh(
		    resp4->umac_data.dbg_ptrs.error_info_addr);
		if (resp4->status == IWX_ALIVE_STATUS_OK)
			sc->sc_uc.uc_ok = 1;
	}
 done:
	sc->sc_uc.uc_intr = 1;
	osdep_event_signal(sc->sc_os, OSDEP_EV_ALIVE);
}

/* Called from iwx_notif_intr() with osdep_lock held. */
void
iwx_rx_pkt(struct iwx_softc *sc, struct iwx_rx_data *data)
{
	const uint8_t *buf = data->buf.vaddr;
	const struct iwx_rx_packet *pkt;
	uint32_t offset = 0, len;
	const size_t minsz = sizeof(pkt->len_n_flags) + sizeof(pkt->hdr);
	int qid, idx, handled;
	uint32_t code;

	while (offset + minsz < IWX_RBUF_SIZE) {
		pkt = (const struct iwx_rx_packet *)(buf + offset);
		qid = pkt->hdr.qid;
		idx = pkt->hdr.idx;
		handled = 1;

		code = IWX_WIDE_ID(pkt->hdr.flags, pkt->hdr.code);

		if (!iwx_rx_pkt_valid(pkt))
			break;

		/*
		 * XXX Intel inside (tm)
		 * Any commands in the LONG_GROUP could actually be in the
		 * LEGACY group. Firmware API versions >= 50 reject commands
		 * in group 0, forcing us to use this hack.
		 */
		if (iwx_cmd_groupid(code) == IWX_LONG_GROUP &&
		    qid == IWX_DQA_CMD_QUEUE && idx < IWX_TX_RING_COUNT) {
			struct iwx_tx_ring *ring = &sc->txq[qid];
			struct iwx_tx_data *txdata = &ring->data[idx];
			if (txdata->flags & IWX_TXDATA_FLAG_CMD_IS_NARROW)
				code = iwx_cmd_opcode(code);
		}

		len = sizeof(pkt->len_n_flags) + iwx_rx_packet_len(pkt);
		if (len < minsz || len > (IWX_RBUF_SIZE - offset))
			break;

		switch (code) {
		case IWX_REPLY_RX_MPDU_CMD: {
			size_t maxlen = IWX_RBUF_SIZE - offset - minsz;
			iwx_rx_mpdu_mq(sc, pkt->data, maxlen);
			break;
		}

		case IWX_ALIVE:
			iwx_rx_alive(sc, pkt);
			break;

		case IWX_INIT_COMPLETE_NOTIF:
			sc->sc_init_complete |= IWX_INIT_COMPLETE;
			osdep_event_signal(sc->sc_os, OSDEP_EV_INIT);
			break;

		case IWX_WIDE_ID(IWX_REGULATORY_AND_NVM_GROUP,
		    IWX_PNVM_INIT_COMPLETE):
			sc->sc_init_complete |= IWX_PNVM_COMPLETE;
			osdep_event_signal(sc->sc_os, OSDEP_EV_INIT);
			break;

		case IWX_WIDE_ID(IWX_PHY_OPS_GROUP,
		    IWX_CT_KILL_NOTIFICATION): {
			const struct iwx_ct_kill_notif *notif =
			    (const void *)pkt->data;
			IWX_ERR(sc, "device at critical temperature (%u degC), "
			    "stopping device\n",
			    (unsigned)le16toh(notif->temperature));
			sc->sc_flags |= IWX_FLAG_HW_ERR;
			if ((sc->sc_flags & IWX_FLAG_SHUTDOWN) == 0)
				osdep_fatal(sc->sc_os, OSDEP_FATAL_CT_KILL);
			break;
		}

		case IWX_REPLY_ERROR: {
			const struct iwx_error_resp *resp =
			    (const void *)pkt->data;
			IWX_ERR(sc, "firmware error 0x%x, cmd 0x%x\n",
			    le32toh(resp->error_type), resp->cmd_id);
			break;
		}

		/* notifications we know and deliberately ignore (D-CMD6) */
		case IWX_REPLY_RX_PHY_CMD:
		case IWX_BAR_FRAME_RELEASE:
		case IWX_TX_CMD:
		case IWX_BA_NOTIF:
		case IWX_MISSED_BEACONS_NOTIFICATION:
		case IWX_WIDE_ID(IWX_MAC_CONF_GROUP, IWX_MISSED_BEACONS_NOTIF):
		case IWX_MFUART_LOAD_NOTIFICATION:
		case IWX_STATISTICS_NOTIFICATION:
		case IWX_DTS_MEASUREMENT_NOTIFICATION:
		case IWX_WIDE_ID(IWX_PHY_OPS_GROUP,
				 IWX_DTS_MEASUREMENT_NOTIF_WIDE):
		case IWX_SCAN_COMPLETE_UMAC:
		case IWX_SCAN_ITERATION_COMPLETE_UMAC:
		case IWX_MCC_CHUB_UPDATE_CMD:
		case IWX_TIME_EVENT_NOTIFICATION:
		case IWX_PSM_UAPSD_AP_MISBEHAVING_NOTIFICATION:
		case IWX_WIDE_ID(IWX_MAC_CONF_GROUP,
		    IWX_SESSION_PROTECTION_NOTIF):
		case IWX_WIDE_ID(IWX_MAC_CONF_GROUP,
		    IWX_CHANNEL_SWITCH_START_NOTIF):
		case IWX_WIDE_ID(IWX_SYSTEM_GROUP,
		    IWX_FSEQ_VER_MISMATCH_NOTIFICATION):
		case IWX_DEBUG_LOG_MSG:
		case IWX_WIDE_ID(IWX_DATA_PATH_GROUP, IWX_RX_NO_DATA_NOTIF):
		case IWX_WIDE_ID(IWX_DATA_PATH_GROUP,
		    IWX_TLC_MNG_UPDATE_NOTIF):
		case IWX_WIDE_ID(IWX_DATA_PATH_GROUP,
		    IWX_THERMAL_DUAL_CHAIN_REQUEST):
		case IWX_WIDE_ID(IWX_DATA_PATH_GROUP, 0xf8):
		case IWX_WIDE_ID(IWX_SYSTEM_GROUP, 0xfc):
		case IWX_WIDE_ID(IWX_SYSTEM_GROUP,
		    IWX_SYSTEM_STATISTICS_END_NOTIF):
		case IWX_WIDE_ID(IWX_STATISTICS_GROUP,
		    IWX_STATISTICS_OPER_NOTIF):
		case IWX_WIDE_ID(IWX_STATISTICS_GROUP,
		    IWX_STATISTICS_OPER_PART1_NOTIF):
		case IWX_WIDE_ID(IWX_BT_COEX_GROUP, IWX_PROFILE_NOTIF):
			break;

		default:
			/* D-CMD4: plain command replies are handled below */
			if (!(qid & 0x80))
				break;
			handled = 0;
			sc->sc_stat_unhandled_notif++;
			DPRINTF(sc, "unhandled firmware notification 0x%x/0x%x "
			    "rx ring %d[%d]\n", code, pkt->len_n_flags,
			    (qid & ~0x80), idx);
			break;
		}

		/*
		 * uCode sets bit 0x80 when it originates the notification,
		 * i.e. when the notification is not a direct response to a
		 * command sent by the driver.
		 */
		if (handled && !(qid & (1 << 7))) {
			iwx_cmd_store_resp(sc, pkt, idx);
			iwx_cmd_done(sc, qid, idx, (int)code);
		}

		offset += roundup(len, IWX_FH_RSCSR_FRAME_ALIGN);

		/* AX210 devices ship only one packet per Rx buffer. */
		if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
			break;
	}
}
