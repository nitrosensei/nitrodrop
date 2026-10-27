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
 * End-to-end bring-up against the device model: attach, context info /
 * IML construction, ALIVE, NVM, init_hw command order, stop/detach.
 * Also command queue (TFD) and RX descriptor checks.
 */

#include "mock.h"
#include "../core/iwx_core.h"

static struct iwx_softc *
attach_sim(const uint8_t *fw, size_t fwlen)
{
	struct iwx_softc *sc;
	int err;

	sim_reset();
	g_sim.fw_data = (uint8_t *)fw;
	g_sim.fw_len = fwlen;
	sc = calloc(1, iwx_softc_size());
	g_sim.sc = sc;
	err = iwx_attach(sc, &g_sim, 0x51f1, 0x0274);
	CHECK_EQ(err, 0);
	if (err) {
		free(sc);
		g_sim.sc = NULL;
		return NULL;
	}
	iwx_set_msix(sc, 1);
	return sc;
}

static void
detach_sim(struct iwx_softc *sc)
{
	iwx_detach(sc);
	free(sc);
	g_sim.sc = NULL;
	CHECK_EQ(g_sim.dma_allocs, g_sim.dma_frees);
	CHECK_EQ(g_sim.mallocs, g_sim.frees);
	CHECK_EQ(g_sim.lock_errors, 0);
	CHECK_EQ(g_sim.lock_depth, 0);
}

void
test_attach_reject(void)
{
	struct iwx_softc *sc;

	printf("test_attach_reject\n");
	sim_reset();
	sc = calloc(1, iwx_softc_size());
	g_sim.sc = sc;
	CHECK_EQ(iwx_attach(sc, &g_sim, 0x2725, 0x0024), ENXIO);	/* AX210 */
	free(sc);

	/* right PCI ID, wrong RF (Gf instead of Hr2) */
	sim_reset();
	{
		uint32_t rf = 0x0010d000;
		memcpy(&g_sim.regs[IWX_CSR_HW_RF_ID], &rf, 4);
	}
	sc = calloc(1, iwx_softc_size());
	g_sim.sc = sc;
	CHECK_EQ(iwx_attach(sc, &g_sim, 0x51f1, 0x0274), ENOTSUP);
	free(sc);
	g_sim.sc = NULL;
	CHECK_EQ(g_sim.dma_allocs, g_sim.dma_frees);
}

void
test_bringup(const uint8_t *fw, size_t fwlen, const char *label)
{
	struct iwx_softc *sc;
	struct iwx_info info;
	int err, i, n;
	/* iwx_init_hw order (D-IN* notes in iwx_init.c) */
	static const struct { uint8_t g, o; const char *n; } order[] = {
		{ IWX_LONG_GROUP, IWX_TX_ANT_CONFIGURATION_CMD, "TX_ANT" },
		{ IWX_LONG_GROUP, IWX_BT_CONFIG, "BT_CONFIG" },
		{ IWX_SYSTEM_GROUP, IWX_SOC_CONFIGURATION_CMD, "SOC_CONFIG" },
		{ IWX_DATA_PATH_GROUP, IWX_DQA_ENABLE_CMD, "DQA_ENABLE" },
		{ IWX_LONG_GROUP, IWX_LTR_CONFIG, "LTR_CONFIG" },
		{ IWX_PHY_OPS_GROUP, IWX_TEMP_REPORTING_THRESHOLDS_CMD, "TEMP" },
		{ IWX_LONG_GROUP, IWX_POWER_TABLE_CMD, "POWER_TABLE" },
		{ IWX_LONG_GROUP, IWX_MCC_UPDATE_CMD, "MCC_UPDATE" },
		{ IWX_LONG_GROUP, IWX_SCAN_CFG_CMD, "SCAN_CFG" },
		{ IWX_LONG_GROUP, IWX_REPLY_BEACON_FILTERING_CMD, "BEACON_FILTER" },
	};

	printf("test_bringup (%s)\n", label);
	sc = attach_sim(fw, fwlen);
	if (sc == NULL)
		return;

	/* -- preinit: INIT image run, NVM read, device stopped again -- */
	err = iwx_preinit(sc);
	CHECK_EQ(err, 0);
	CHECK_EQ(g_sim.boots, 1);
	CHECK_EQ(g_sim.fw_loads, 1);
	CHECK(g_sim.ctxt_ok);
	if (!g_sim.ctxt_ok)
		printf("  context info error: %s\n", g_sim.ctxt_err);
	/* So+Hr has no PNVM file, but iwx still rings the PNVM doorbell
	 * (sku_id != 0) and waits for PNVM_INIT_COMPLETE */
	CHECK_EQ(g_sim.pnvm_doorbells, 1);
	n = sim_find_cmd(IWX_REGULATORY_AND_NVM_GROUP, IWX_NVM_ACCESS_COMPLETE);
	CHECK(n >= 0);
	CHECK(sim_find_cmd(IWX_REGULATORY_AND_NVM_GROUP, IWX_NVM_GET_INFO) > n);
	CHECK(memcmp(sc->sc_nvm.hw_addr, g_sim.mac, 6) == 0);
	CHECK(sc->sc_nvm.nchan > 13);
	CHECK_EQ(sc->sc_nvm.lar_enabled, 1);
	CHECK_EQ(g_sim.fatal_count, 0);

	/* the IML went through CSR registers, ctxt info address too */
	{
		uint32_t v;
		memcpy(&v, &g_sim.regs[IWX_CSR_IML_SIZE_ADDR], 4);
		CHECK_EQ(v, sc->sc_fw.iml_len);
	}
	/* context info "version" fields as iwlwifi sets them */
	CHECK_EQ(g_sim.scratch_ctrl.version.version, 0);
	CHECK(g_sim.lmac0 != 0 && g_sim.umac0 != 0);

	/* -- full init: second boot, init_hw command sequence -- */
	g_sim.ncmds = 0;
	err = iwx_init(sc);
	CHECK_EQ(err, 0);
	CHECK_EQ(g_sim.boots, 2);
	CHECK_EQ(g_sim.tfd_errors, 0);
	CHECK_EQ(g_sim.fatal_count, 0);
	{
		int last = -1, ok = 1;
		for (i = 0; i < (int)nitems(order); i++) {
			int at = sim_find_cmd(order[i].g, order[i].o);
			/* iwx sends DQA_ENABLE only with CAPA_DQA_SUPPORT */
			if (order[i].o == IWX_DQA_ENABLE_CMD &&
			    order[i].g == IWX_DATA_PATH_GROUP &&
			    !isset(sc->sc_enabled_capa,
			    IWX_UCODE_TLV_CAPA_DQA_SUPPORT)) {
				CHECK_EQ(at, -1);
				continue;
			}
			if (at < 0 || at < last) {
				printf("  init_hw order: %s at %d (prev %d)\n",
				    order[i].n, at, last);
				ok = 0;
			} else
				last = at;
		}
		CHECK(ok);
	}
	/* MCC "ZZ" */
	n = sim_find_cmd(IWX_LONG_GROUP, IWX_MCC_UPDATE_CMD);
	if (n >= 0) {
		CHECK_EQ(g_sim.cmds[n].payload[0], 'Z');
		CHECK_EQ(g_sim.cmds[n].payload[1], 'Z');
	}
	/* power: CAM (D-IN4) - flags field must not request power save */
	n = sim_find_cmd(IWX_LONG_GROUP, IWX_POWER_TABLE_CMD);
	if (n >= 0)
		CHECK_EQ(g_sim.cmds[n].payload[0] & 0x01, 0);

	iwx_get_info(sc, &info);
	CHECK_EQ(info.state, IWX_STATE_FW_READY);
	CHECK(memcmp(info.macaddr, g_sim.mac, 6) == 0);
	CHECK(strlen(info.fwver) > 0);
	CHECK(info.nchan > 0 && info.chan[0] == 1);
	CHECK_EQ(info.msix, 1);

	/* M1 radio stubs */
	CHECK_EQ(iwx_radio_start(sc, 6, NULL, 0), ENOTSUP);
	CHECK_EQ(iwx_radio_start(sc, 0, NULL, 0), EINVAL);
	CHECK_EQ(iwx_radio_start(sc, 200, NULL, 0), EINVAL);

	iwx_stop(sc);
	iwx_get_info(sc, &info);
	CHECK_EQ(info.state, IWX_STATE_DOWN);

	/* restart after stop works (re-runs the regular image) */
	g_sim.ncmds = 0;
	CHECK_EQ(iwx_init(sc), 0);
	CHECK_EQ(g_sim.boots, 3);
	iwx_stop(sc);

	detach_sim(sc);
}

/* Firmware that never answers -> timeouts, no hangs, no leaks. */
void
test_failures(const uint8_t *fw, size_t fwlen)
{
	struct iwx_softc *sc;

	printf("test_failures\n");

	/* ALIVE with a bad status */
	sc = attach_sim(fw, fwlen);
	if (sc == NULL)
		return;
	g_sim.alive_status = 0xdead;
	CHECK(iwx_preinit(sc) != 0);
	detach_sim(sc);

	/* commands never answered */
	sc = attach_sim(fw, fwlen);
	if (sc == NULL)
		return;
	g_sim.drop_responses = 1;
	CHECK_EQ(iwx_preinit(sc), ETIMEDOUT);
	detach_sim(sc);

	/* NVM_GET_INFO answered with CMD_FAILED */
	sc = attach_sim(fw, fwlen);
	if (sc == NULL)
		return;
	g_sim.fail_cmd_group = IWX_REGULATORY_AND_NVM_GROUP;
	g_sim.fail_cmd_opcode = IWX_NVM_GET_INFO;
	CHECK(iwx_preinit(sc) != 0);
	detach_sim(sc);

	/* no firmware file */
	sc = attach_sim(NULL, 0);
	if (sc == NULL)
		return;
	CHECK_EQ(iwx_preinit(sc), ENOENT);
	detach_sim(sc);

	/* ALIVE whose size does not match the advertised notif version */
	sc = attach_sim(fw, fwlen);
	if (sc == NULL)
		return;
	g_sim.alive_ver = 5;
	CHECK(iwx_preinit(sc) != 0);
	detach_sim(sc);
}

/* ---- command queue / TFD ring ------------------------------------------ */

void
test_cmd_queue(const uint8_t *fw, size_t fwlen)
{
	struct iwx_softc *sc;
	struct iwx_tx_ring *ring;
	struct iwx_host_cmd hcmd;
	struct iwx_tfh_tfd *desc;
	uint8_t big[1200];
	uint32_t status;
	uint64_t a0, a1;
	int err, idx, i;

	printf("test_cmd_queue\n");
	sc = attach_sim(fw, fwlen);
	if (sc == NULL)
		return;
	CHECK_EQ(iwx_preinit(sc), 0);
	CHECK_EQ(iwx_init(sc), 0);
	ring = &sc->txq[IWX_DQA_CMD_QUEUE];

	/* small sync command with status: one TB */
	idx = ring->cur;
	g_sim.ncmds = 0;
	status = 0xffffffff;
	err = iwx_send_cmd_pdu_status(sc, IWX_WIDE_ID(IWX_LONG_GROUP,
	    IWX_LTR_CONFIG), 8, big, &status);
	CHECK_EQ(err, 0);
	CHECK_EQ(status, 0);
	desc = &((struct iwx_tfh_tfd *)ring->desc)[idx];
	CHECK_EQ(le16toh(desc->num_tbs), 1);
	CHECK_EQ(le16toh(desc->tbs[0].tb_len),
	    sizeof(struct iwx_cmd_header_wide) + 8);
	memcpy(&a0, &desc->tbs[0].addr, 8);
	CHECK_EQ(a0, ring->cmd_dma.paddr + (uint64_t)idx * sizeof(struct iwx_device_cmd));
	CHECK_EQ(ring->cur, (idx + 1) % IWX_TX_RING_COUNT);
	CHECK_EQ(g_sim.ncmds, 1);
	CHECK_EQ(g_sim.cmds[0].version, 0);
	CHECK_EQ(ring->queued, 0);

	/* legacy-group (narrow) id is sent as LONG_GROUP with wide header */
	g_sim.ncmds = 0;
	err = iwx_send_cmd_pdu(sc, IWX_POWER_TABLE_CMD, 0, 4, big);
	CHECK_EQ(err, 0);
	CHECK_EQ(g_sim.cmds[0].group, IWX_LONG_GROUP);
	CHECK_EQ(g_sim.cmds[0].opcode, IWX_POWER_TABLE_CMD);

	/* large command: header+payload > TB0 -> two TBs from cmd_big_dma */
	for (i = 0; i < (int)sizeof(big); i++)
		big[i] = (uint8_t)i;
	idx = ring->cur;
	g_sim.ncmds = 0;
	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.id = IWX_WIDE_ID(IWX_LONG_GROUP, IWX_SCAN_CFG_CMD);
	hcmd.len[0] = sizeof(big);
	hcmd.data[0] = big;
	err = iwx_send_cmd(sc, &hcmd);
	CHECK_EQ(err, 0);
	desc = &((struct iwx_tfh_tfd *)ring->desc)[idx];
	CHECK_EQ(le16toh(desc->num_tbs), 2);
	CHECK_EQ(le16toh(desc->tbs[0].tb_len), IWX_FIRST_TB_SIZE);
	CHECK_EQ(le16toh(desc->tbs[1].tb_len),
	    sizeof(struct iwx_cmd_header_wide) + sizeof(big) - IWX_FIRST_TB_SIZE);
	memcpy(&a0, &desc->tbs[0].addr, 8);
	memcpy(&a1, &desc->tbs[1].addr, 8);
	CHECK_EQ(a0, sc->cmd_big_dma.paddr);
	CHECK_EQ(a1, sc->cmd_big_dma.paddr + IWX_FIRST_TB_SIZE);
	CHECK_EQ(g_sim.ncmds, 1);
	CHECK_EQ(g_sim.cmds[0].paylen, sizeof(big));
	CHECK(memcmp(g_sim.cmds[0].payload, big, 64) == 0);
	CHECK_EQ(sc->cmd_big_idx, -1);		/* released on completion */
	CHECK_EQ(g_sim.tfd_errors, 0);

	/* too large for the bounce buffer */
	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.id = IWX_WIDE_ID(IWX_LONG_GROUP, IWX_SCAN_CFG_CMD);
	hcmd.len[0] = 5000;
	hcmd.data[0] = big;
	CHECK(iwx_send_cmd(sc, &hcmd) != 0);

	/* response copy: NVM_GET_INFO with WANT_RESP */
	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.id = IWX_WIDE_ID(IWX_REGULATORY_AND_NVM_GROUP, IWX_NVM_GET_INFO);
	hcmd.flags = IWX_CMD_WANT_RESP;
	hcmd.resp_pkt_len = sizeof(struct iwx_rx_packet) +
	    sizeof(struct iwx_nvm_get_info_rsp);
	err = iwx_send_cmd(sc, &hcmd);
	CHECK_EQ(err, 0);
	CHECK(hcmd.resp_pkt != NULL);
	if (hcmd.resp_pkt != NULL) {
		struct iwx_nvm_get_info_rsp *r = (void *)hcmd.resp_pkt->data;
		CHECK_EQ(iwx_rx_packet_payload_len(hcmd.resp_pkt),
		    sizeof(struct iwx_nvm_get_info_rsp));
		CHECK_EQ(r->general.nvm_version, 0x0a1d);
	}
	iwx_free_resp(sc, &hcmd);

	/* command ring wraps cleanly past IWX_TX_RING_COUNT */
	g_sim.ncmds = 0;
	for (i = 0; i < IWX_TX_RING_COUNT + 10; i++) {
		err = iwx_send_cmd_pdu(sc, IWX_WIDE_ID(IWX_LONG_GROUP,
		    IWX_LTR_CONFIG), 0, 8, big);
		if (err)
			break;
		if (g_sim.ncmds >= SIM_MAX_CMDS - 1)
			g_sim.ncmds = 0;
	}
	CHECK_EQ(err, 0);
	CHECK_EQ(g_sim.tfd_errors, 0);
	CHECK_EQ(ring->queued, 0);

	/* async command: no response wait, completion still drains ring */
	err = iwx_send_cmd_pdu(sc, IWX_WIDE_ID(IWX_LONG_GROUP,
	    IWX_LTR_CONFIG), IWX_CMD_ASYNC, 8, big);
	CHECK_EQ(err, 0);
	sim_service_interrupts();
	CHECK_EQ(ring->queued, 0);

	/* fatal error wakes a waiter with EIO-class error */
	g_sim.drop_responses = 1;
	sc->sc_flags |= IWX_FLAG_FW_ERR;
	err = iwx_send_cmd_pdu(sc, IWX_WIDE_ID(IWX_LONG_GROUP,
	    IWX_LTR_CONFIG), 0, 8, big);
	CHECK(err != 0);
	sc->sc_flags &= ~IWX_FLAG_FW_ERR;
	g_sim.drop_responses = 0;

	iwx_stop(sc);
	detach_sim(sc);
}

/* ---- RX ring ------------------------------------------------------------ */

void
test_rx_ring(const uint8_t *fw, size_t fwlen)
{
	struct iwx_softc *sc;
	struct iwx_rx_transfer_desc *fd;
	struct iwx_rx_ring *ring;
	int i, before;

	printf("test_rx_ring\n");
	sc = attach_sim(fw, fwlen);
	if (sc == NULL)
		return;
	ring = &sc->rxq;

	/* free RBD table: rbid == index, 4K-aligned buffers, 64-bit addr */
	fd = ring->free_desc_dma.vaddr;
	for (i = 0; i < IWX_RX_MQ_RING_COUNT; i++) {
		CHECK_EQ(fd[i].rbid, i);
		CHECK(fd[i].addr != 0);
		CHECK_EQ(fd[i].addr & 0xfff, 0);
		if (fd[i].rbid != i || fd[i].addr == 0)
			break;
	}
	CHECK_EQ(sizeof(struct iwx_rx_transfer_desc), 16);
	CHECK_EQ(sizeof(struct iwx_rx_completion_desc), 32);
	CHECK_EQ(ring->free_desc_dma.paddr & 0xff, 0);
	CHECK_EQ(ring->used_desc_dma.paddr & 0xff, 0);

	CHECK_EQ(iwx_preinit(sc), 0);
	CHECK_EQ(iwx_init(sc), 0);

	/* drive many notifications through to wrap the 512-entry ring */
	before = ring->cur;
	for (i = 0; i < IWX_RX_MQ_RING_COUNT + 37; i++) {
		uint32_t dummy = (uint32_t)i;
		/* unknown notification on the notif "queue": ignored */
		sim_rx_packet(IWX_LEGACY_GROUP, 0xfb, 0x80, 0, &dummy, 4);
		sim_service_interrupts();
	}
	CHECK_EQ(ring->cur, (before + IWX_RX_MQ_RING_COUNT + 37) %
	    IWX_RX_MQ_RING_COUNT);
	/* FRBDCB write index: (hw - 1) & ~7, as iwx/iwlwifi */
	CHECK_EQ(g_sim.last_frbdcb_widx, ((uint32_t)ring->cur - 1) & ~7u &
	    (IWX_RX_MQ_RING_COUNT - 1));
	CHECK_EQ(g_sim.tfd_errors, 0);

	/* a monitor MPDU with RX disabled is dropped silently */
	{
		uint8_t buf[sizeof(struct iwx_rx_mpdu_desc) + 64];
		struct iwx_rx_mpdu_desc *d = (void *)buf;
		uint8_t *f = buf + sizeof(*d);

		memset(buf, 0, sizeof(buf));
		d->mpdu_len = htole16(30);
		d->status = htole32(IWX_RX_MPDU_RES_STATUS_CRC_OK |
		    IWX_RX_MPDU_RES_STATUS_OVERRUN_OK);
		d->v3.channel = 6;
		d->v3.energy_a = 40;
		d->v3.energy_b = 50;
		d->v3.gp2_on_air_rise = htole32(123456);
		f[0] = 0x80;			/* beacon */
		memset(f + 4, 0xff, 6);
		f[10] = 0x00; f[11] = 0x25; f[12] = 0x00;
		f[13] = 0xff; f[14] = 0x94; f[15] = 0x73;

		sc->sc_rx_enabled = 0;
		g_sim.rx_frames = 0;
		sim_rx_packet(IWX_LEGACY_GROUP, IWX_REPLY_RX_MPDU_CMD, 0x80,
		    0, buf, sizeof(*d) + 30);
		sim_service_interrupts();
		CHECK_EQ(g_sim.rx_frames, 0);

		sc->sc_rx_enabled = 1;
		sim_rx_packet(IWX_LEGACY_GROUP, IWX_REPLY_RX_MPDU_CMD, 0x80,
		    0, buf, sizeof(*d) + 30);
		sim_service_interrupts();
		CHECK_EQ(g_sim.rx_frames, 1);
		CHECK_EQ(g_sim.last_frame_len, 30);
		CHECK_EQ(g_sim.last_frame[0], 0x80);
		CHECK_EQ(g_sim.last_frame[15], 0x73);
		CHECK_EQ(g_sim.last_meta.channel, 6);
		CHECK_EQ(g_sim.last_meta.gp2, 123456);
		CHECK_EQ(g_sim.last_meta.rssi_dbm, -40);	/* max(a,b) */

		/* CRC failed -> dropped, counted */
		d->status = htole32(IWX_RX_MPDU_RES_STATUS_OVERRUN_OK);
		sim_rx_packet(IWX_LEGACY_GROUP, IWX_REPLY_RX_MPDU_CMD, 0x80,
		    0, buf, sizeof(*d) + 30);
		sim_service_interrupts();
		CHECK_EQ(g_sim.rx_frames, 1);

		/* header pad: 24-byte data header + 2 pad bytes removed */
		memset(buf, 0, sizeof(buf));
		d->mpdu_len = htole16(32);
		d->status = htole32(IWX_RX_MPDU_RES_STATUS_CRC_OK |
		    IWX_RX_MPDU_RES_STATUS_OVERRUN_OK);
		d->mac_flags2 = IWX_RX_MPDU_MFLG2_PAD;
		d->v3.channel = 6;
		f[0] = 0x08;			/* data */
		for (i = 0; i < 24; i++)
			f[i + 1] = (f[0] == 0x08 && i == 0) ? 0 : (uint8_t)i;
		f[24] = 0xee; f[25] = 0xee;	/* pad */
		f[26] = 0xaa; f[27] = 0xbb;	/* payload */
		sim_rx_packet(IWX_LEGACY_GROUP, IWX_REPLY_RX_MPDU_CMD, 0x80,
		    0, buf, sizeof(*d) + 32);
		sim_service_interrupts();
		CHECK_EQ(g_sim.rx_frames, 2);
		CHECK_EQ(g_sim.last_frame_len, 30);
		CHECK_EQ(g_sim.last_frame[24], 0xaa);
		CHECK_EQ(g_sim.last_frame[25], 0xbb);
	}

	iwx_stop(sc);
	detach_sim(sc);
}
