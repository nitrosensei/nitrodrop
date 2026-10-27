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
 * M2 (monitor RX) and M3 (IBSS + injection) against the device model,
 * driven by the real -77 firmware image (command versions matter here).
 *
 * Expected payloads are built field by field from the iwx/iwlwifi
 * structure definitions in iwx_reg.h (NOT through the driver's own fill
 * helpers) and byte-compared with what the "device" fetched from the
 * command queue.
 */

#include "mock.h"
#include "../core/iwx_core.h"

static const uint8_t owly_bssid[6] = { 0x00, 0x25, 0x00, 0xff, 0x94, 0x73 };

static struct iwx_softc *
radio_attach(const uint8_t *fw, size_t fwlen)
{
	struct iwx_softc *sc;

	sim_reset();
	g_sim.fw_data = (uint8_t *)fw;
	g_sim.fw_len = fwlen;
	sc = calloc(1, iwx_softc_size());
	g_sim.sc = sc;
	if (iwx_attach(sc, &g_sim, 0x51f1, 0x0274) != 0) {
		CHECK(0);
		free(sc);
		g_sim.sc = NULL;
		return NULL;
	}
	iwx_set_msix(sc, 1);
	CHECK_EQ(iwx_init(sc), 0);
	return sc;
}

static void
radio_detach(struct iwx_softc *sc)
{
	iwx_detach(sc);
	free(sc);
	g_sim.sc = NULL;
	CHECK_EQ(g_sim.dma_allocs, g_sim.dma_frees);
	CHECK_EQ(g_sim.mallocs, g_sim.frees);
	CHECK_EQ(g_sim.lock_errors, 0);
	CHECK_EQ(g_sim.lock_depth, 0);
	CHECK_EQ(g_sim.txst_unlocked, 0);
}

struct seq { uint8_t group, opcode; };
#define L(op)	{ IWX_LONG_GROUP, op }
#define D(op)	{ IWX_DATA_PATH_GROUP, op }

static int
expect_seq(int base, const struct seq *s, int n, const char *what)
{
	int i, ok = 1;

	CHECK_EQ(g_sim.ncmds - base, n);
	if (g_sim.ncmds - base != n)
		ok = 0;
	for (i = 0; i < n && base + i < g_sim.ncmds; i++) {
		const struct sim_cmd *c = &g_sim.cmds[base + i];
		if (c->group != s[i].group || c->opcode != s[i].opcode) {
			printf("  %s: cmd %d is %02x/%02x, want %02x/%02x\n",
			    what, i, c->group, c->opcode, s[i].group,
			    s[i].opcode);
			ok = 0;
		}
	}
	CHECK(ok);
	return ok;
}

static void
expect_payload(int i, const void *exp, size_t len, const char *what)
{
	const struct sim_cmd *c;

	CHECK(i >= 0 && i < g_sim.ncmds);
	if (i < 0 || i >= g_sim.ncmds)
		return;
	c = &g_sim.cmds[i];
	CHECK_EQ(c->paylen, len);
	if (memcmp(c->payload, exp, MIN(len, sizeof(c->payload))) != 0) {
		size_t k;

		for (k = 0; k < MIN(len, sizeof(c->payload)); k++)
			if (c->payload[k] != ((const uint8_t *)exp)[k])
				break;
		printf("  FAIL %s: payload differs at byte %u (0x%02x != "
		    "0x%02x)\n", what, (unsigned)k, c->payload[k],
		    ((const uint8_t *)exp)[k]);
		CHECK(0);
	} else
		CHECK(1);
}

/* ---- expected payloads (iwx_reg.h structures) -------------------------- */

static void
exp_phy(struct iwx_phy_context_cmd_uhb *p, uint32_t action, uint8_t chan)
{
	memset(p, 0, sizeof(*p));
	p->id_and_color = 0;		/* phy 0, color 0 */
	p->action = action;
	p->ci.channel = chan;
	p->ci.band = IWX_PHY_BAND_24;
	p->ci.width = IWX_PHY_VHT_CHANNEL_MODE20;
	p->ci.ctrl_pos = IWX_PHY_VHT_CTRL_POS_1_BELOW;
	p->lmac_id = IWX_LMAC_24G_INDEX;
	p->rxchain_info = 0;		/* RLC_CONFIG v2 carries it */
}

static void
exp_rlc(struct iwx_rlc_config_cmd *r, uint32_t chains)
{
	memset(r, 0, sizeof(*r));
	r->phy_id = 0;
	/* valid_rx_ant = A|B (phy_config 0x00330018) */
	r->rlc.rx_chain_info = (3u << IWX_PHY_RX_CHAIN_VALID_POS) |
	    (chains << IWX_PHY_RX_CHAIN_CNT_POS) |
	    (chains << IWX_PHY_RX_CHAIN_MIMO_CNT_POS);
}

static void
exp_mac_listener(struct iwx_mac_ctx_cmd *m, uint32_t action, int assoc)
{
	memset(m, 0, sizeof(*m));
	m->id_and_color = 0;
	m->action = action;
	if (action == IWX_FW_CTXT_ACTION_REMOVE)
		return;
	m->mac_type = IWX_FW_MAC_TYPE_LISTENER;
	m->tsf_id = IWX_TSF_ID_A;
	memcpy(m->node_addr, g_sim.mac, 6);
	memset(m->bssid_addr, 0xff, 6);
	m->filter_flags = IWX_MAC_FILTER_IN_PROMISC |
	    IWX_MAC_FILTER_IN_CONTROL_AND_MGMT | IWX_MAC_FILTER_ACCEPT_GRP |
	    IWX_MAC_FILTER_IN_BEACON | IWX_MAC_FILTER_IN_PROBE_REQUEST |
	    IWX_MAC_FILTER_IN_CRC32;
	m->sta.is_assoc = (uint32_t)assoc;
	m->sta.listen_interval = 10;
}

static void
exp_mac_ibss(struct iwx_mac_ctx_cmd *m, uint32_t action, int promisc)
{
	memset(m, 0, sizeof(*m));
	m->action = action;
	if (action == IWX_FW_CTXT_ACTION_REMOVE)
		return;
	m->mac_type = IWX_FW_MAC_TYPE_IBSS;
	m->tsf_id = IWX_TSF_ID_A;
	memcpy(m->node_addr, g_sim.mac, 6);
	memcpy(m->bssid_addr, owly_bssid, 6);
	m->cck_rates = 0x01;
	m->ofdm_rates = 0x15;
	m->cck_short_preamble = IWX_MAC_FLG_SHORT_PREAMBLE;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_BE].cw_min = 15;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_BE].cw_max = 1023;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_BE].aifsn = 3;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_BE].fifos_mask =
	    1 << IWX_GEN2_EDCA_TX_FIFO_BE;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_BK].cw_min = 15;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_BK].cw_max = 1023;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_BK].aifsn = 7;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_BK].fifos_mask =
	    1 << IWX_GEN2_EDCA_TX_FIFO_BK;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VI].cw_min = 7;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VI].cw_max = 15;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VI].aifsn = 2;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VI].fifos_mask =
	    1 << IWX_GEN2_EDCA_TX_FIFO_VI;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VI].edca_txop = 94 * 32;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VO].cw_min = 3;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VO].cw_max = 7;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VO].aifsn = 2;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VO].fifos_mask =
	    1 << IWX_GEN2_EDCA_TX_FIFO_VO;
	m->ac[IWX_GEN2_EDCA_TX_FIFO_VO].edca_txop = 47 * 32;
	m->qos_flags = IWX_MAC_QOS_FLG_UPDATE_EDCA | IWX_MAC_QOS_FLG_TGN;
	m->filter_flags = IWX_MAC_FILTER_IN_BEACON |
	    IWX_MAC_FILTER_IN_PROBE_REQUEST | IWX_MAC_FILTER_ACCEPT_GRP;
	if (promisc)
		m->filter_flags |= IWX_MAC_FILTER_IN_PROMISC |
		    IWX_MAC_FILTER_IN_CONTROL_AND_MGMT |
		    IWX_MAC_FILTER_IN_CRC32;
	m->ibss.bi = IWX_RADIO_DEFAULT_BINT;
	m->ibss.beacon_template = 0;
}

static void
exp_binding(struct iwx_binding_cmd *b, uint32_t action)
{
	int i;

	memset(b, 0, sizeof(*b));
	b->id_and_color = 0;
	b->action = action;
	b->macs[0] = 0;
	for (i = 1; i < IWX_MAX_MACS_IN_BINDING; i++)
		b->macs[i] = IWX_FW_CTXT_INVALID;
	b->phy = 0;
	b->lmac_id = IWX_LMAC_24G_INDEX;
}

static void
exp_add_sta(struct iwx_add_sta_cmd *a, int sta, int type, const uint8_t *addr,
    int update, int listener)
{
	memset(a, 0, sizeof(*a));
	a->sta_id = (uint8_t)sta;
	a->station_type = (uint8_t)type;
	a->mac_id_n_color = 0;
	if (addr != NULL)
		memcpy(a->addr, addr, 6);
	a->add_modify = (uint8_t)update;
	if (listener)
		a->station_flags_msk = IWX_STA_FLG_FAT_EN_MSK |
		    IWX_STA_FLG_MIMO_EN_MSK;
	else
		a->tid_disable_tx = 0xffff;
}

static void
exp_drain(struct iwx_add_sta_cmd *a, int sta, int drain)
{
	memset(a, 0, sizeof(*a));
	a->sta_id = (uint8_t)sta;
	a->add_modify = IWX_STA_MODE_MODIFY;
	a->station_flags = drain ? IWX_STA_FLG_DRAIN_FLOW : 0;
	a->station_flags_msk = IWX_STA_FLG_DRAIN_FLOW;
}

static void
exp_flush(struct iwx_tx_path_flush_cmd *f, int sta)
{
	memset(f, 0, sizeof(*f));
	f->sta_id = (uint32_t)sta;
	f->tid_mask = 0xffff;
}

static void
exp_scd_add(struct iwx_scd_queue_cfg_cmd *c, struct iwx_softc *sc, int qid,
    int sta, int tid)
{
	memset(c, 0, sizeof(*c));
	c->operation = IWX_SCD_QUEUE_ADD;
	c->u.add.sta_mask = 1u << sta;
	c->u.add.tid = (uint8_t)tid;
	c->u.add.flags = 0;
	c->u.add.cb_size = IWX_TFD_QUEUE_CB_SIZE(IWX_TX_RING_COUNT);
	c->u.add.bc_dram_addr = sc->txq[qid].bc_tbl.paddr;
	c->u.add.tfdq_dram_addr = sc->txq[qid].desc_dma.paddr;
}

static void
exp_scd_rm(struct iwx_scd_queue_cfg_cmd *c, int sta, int tid)
{
	memset(c, 0, sizeof(*c));
	c->operation = IWX_SCD_QUEUE_REMOVE;
	c->u.remove.sta_mask = 1u << sta;
	c->u.remove.tid = (uint32_t)tid;
}

static void
exp_sf(struct iwx_sf_cfg_cmd *s, uint32_t state)
{
	static const uint32_t full[IWX_SF_NUM_SCENARIO][2] = {
		{ IWX_SF_SINGLE_UNICAST_AGING_TIMER,
		  IWX_SF_SINGLE_UNICAST_IDLE_TIMER },
		{ IWX_SF_AGG_UNICAST_AGING_TIMER, IWX_SF_AGG_UNICAST_IDLE_TIMER },
		{ IWX_SF_MCAST_AGING_TIMER, IWX_SF_MCAST_IDLE_TIMER },
		{ IWX_SF_BA_AGING_TIMER, IWX_SF_BA_IDLE_TIMER },
		{ IWX_SF_TX_RE_AGING_TIMER, IWX_SF_TX_RE_IDLE_TIMER },
	};
	static const uint32_t def[IWX_SF_NUM_SCENARIO][2] = {
		{ IWX_SF_SINGLE_UNICAST_AGING_TIMER_DEF,
		  IWX_SF_SINGLE_UNICAST_IDLE_TIMER_DEF },
		{ IWX_SF_AGG_UNICAST_AGING_TIMER_DEF,
		  IWX_SF_AGG_UNICAST_IDLE_TIMER_DEF },
		{ IWX_SF_MCAST_AGING_TIMER_DEF, IWX_SF_MCAST_IDLE_TIMER_DEF },
		{ IWX_SF_BA_AGING_TIMER_DEF, IWX_SF_BA_IDLE_TIMER_DEF },
		{ IWX_SF_TX_RE_AGING_TIMER_DEF, IWX_SF_TX_RE_IDLE_TIMER_DEF },
	};
	int i, j;

	memset(s, 0, sizeof(*s));
	s->state = state;
	s->watermark[IWX_SF_LONG_DELAY_ON] = IWX_SF_W_MARK_SCAN;
	s->watermark[IWX_SF_FULL_ON] = state == IWX_SF_FULL_ON ?
	    IWX_SF_W_MARK_LEGACY : IWX_SF_W_MARK_MIMO2;
	for (i = 0; i < IWX_SF_NUM_SCENARIO; i++)
		for (j = 0; j < IWX_SF_NUM_TIMEOUT_TYPES; j++) {
			s->long_delay_timeouts[i][j] =
			    IWX_SF_LONG_DELAY_AGING_TIMER;
			s->full_on_timeouts[i][j] = state == IWX_SF_FULL_ON ?
			    full[i][j] : def[i][j];
		}
}

/* IBSS beacon template: struct iwl_mac_beacon_cmd (v10+) + frame */
static size_t
exp_beacon(uint8_t *b, uint8_t chan)
{
	static const uint8_t tail[] = {
		0x10, 0x27,			/* interval 10000 TU */
		0x02, 0x00,			/* capability: IBSS */
		0x00, 0x04, 'o', 'w', 'l', 'y',	/* SSID */
		0x01, 0x08, 0x8c, 0x12, 0x98, 0x24, 0xb0, 0x48, 0x60, 0x6c,
		0x03, 0x01, 0x00,		/* DS parameter set */
		0x06, 0x02, 0x00, 0x00,		/* IBSS parameter set */
	};
	uint8_t *f = b + 32;
	size_t n = 0;

	memset(b, 0, 32 + 64);
	f[n++] = 0x80;
	f[n++] = 0x00;
	n += 2;
	memset(f + n, 0xff, 6);
	n += 6;
	memcpy(f + n, g_sim.mac, 6);
	n += 6;
	memcpy(f + n, owly_bssid, 6);
	n += 6;
	n += 2 + 8;
	memcpy(f + n, tail, sizeof(tail));
	f[n + 22] = chan;
	n += sizeof(tail);
	b[0] = (uint8_t)n;		/* byte_cnt */
	/* flags 0 (6 Mb/s OFDM), short_ssid, link_id 0, TIM/CSA 0 */
	return 32 + n;
}

/* ---- M2 --------------------------------------------------------------- */

static const struct seq m2_up[] = {
	L(IWX_PHY_CONTEXT_CMD), D(IWX_RLC_CONFIG_CMD),
	L(IWX_MAC_CONTEXT_CMD), L(IWX_BINDING_CONTEXT_CMD), L(IWX_ADD_STA),
	D(IWX_SCD_QUEUE_CONFIG_CMD),
	L(IWX_PHY_CONTEXT_CMD), D(IWX_RLC_CONFIG_CMD), L(IWX_ADD_STA),
	L(IWX_MAC_CONTEXT_CMD), L(IWX_REPLY_SF_CFG_CMD),
	L(IWX_MCAST_FILTER_CMD),
};
static const struct seq m2_down[] = {
	L(IWX_ADD_STA), L(IWX_TXPATH_FLUSH), L(IWX_ADD_STA),
	L(IWX_REPLY_SF_CFG_CMD), L(IWX_REPLY_BEACON_FILTERING_CMD),
	L(IWX_MAC_CONTEXT_CMD),
	L(IWX_ADD_STA), L(IWX_TXPATH_FLUSH), L(IWX_ADD_STA),
	D(IWX_SCD_QUEUE_CONFIG_CMD), L(IWX_REMOVE_STA),
	L(IWX_BINDING_CONTEXT_CMD), L(IWX_MAC_CONTEXT_CMD),
	L(IWX_PHY_CONTEXT_CMD),
};

static void
check_m2_sequences(struct iwx_softc *sc)
{
	struct iwx_radio_params rp;
	union {
		struct iwx_phy_context_cmd_uhb phy;
		struct iwx_rlc_config_cmd rlc;
		struct iwx_mac_ctx_cmd mac;
		struct iwx_binding_cmd bind;
		struct iwx_add_sta_cmd sta;
		struct iwx_scd_queue_cfg_cmd scd;
		struct iwx_sf_cfg_cmd sf;
		struct iwx_tx_path_flush_cmd flush;
		struct iwx_rm_sta_cmd rm;
		struct iwx_beacon_filter_cmd bf;
		uint8_t raw[512];
	} e;
	static const uint8_t zero6[6] = { 0 };
	struct iwx_info info;
	int b;

	memset(&rp, 0, sizeof(rp));
	rp.channel = 6;
	rp.flags = IWX_RADIO_F_LISTENER_ONLY;
	b = g_sim.ncmds;
	CHECK_EQ(iwx_radio_start(sc, &rp), 0);
	if (!expect_seq(b, m2_up, (int)nitems(m2_up), "M2 start"))
		return;

	exp_phy(&e.phy, IWX_FW_CTXT_ACTION_ADD, 6);
	expect_payload(b + 0, &e.phy, sizeof(e.phy), "PHY add");
	exp_rlc(&e.rlc, 1);
	expect_payload(b + 1, &e.rlc, sizeof(e.rlc), "RLC 1x1");
	CHECK_EQ(g_sim.cmds[b + 1].version, 2);
	exp_mac_listener(&e.mac, IWX_FW_CTXT_ACTION_ADD, 0);
	expect_payload(b + 2, &e.mac, sizeof(e.mac), "MAC add listener");
	exp_binding(&e.bind, IWX_FW_CTXT_ACTION_ADD);
	expect_payload(b + 3, &e.bind, sizeof(e.bind), "BINDING add");
	exp_add_sta(&e.sta, IWX_MONITOR_STA_ID, IWX_STA_GENERAL_PURPOSE, zero6,
	    0, 1);
	expect_payload(b + 4, &e.sta, sizeof(e.sta), "ADD_STA monitor");
	exp_scd_add(&e.scd, sc, IWX_DQA_INJECT_MONITOR_QUEUE,
	    IWX_MONITOR_STA_ID, IWX_MGMT_TID);
	expect_payload(b + 5, &e.scd, sizeof(e.scd), "SCD add q1");
	CHECK_EQ(g_sim.cmds[b + 5].version, 0);	/* iwx: no version */
	exp_phy(&e.phy, IWX_FW_CTXT_ACTION_MODIFY, 6);
	expect_payload(b + 6, &e.phy, sizeof(e.phy), "PHY modify");
	exp_rlc(&e.rlc, 2);
	expect_payload(b + 7, &e.rlc, sizeof(e.rlc), "RLC 2x2");
	exp_add_sta(&e.sta, IWX_MONITOR_STA_ID, IWX_STA_GENERAL_PURPOSE, NULL,
	    1, 1);
	expect_payload(b + 8, &e.sta, sizeof(e.sta), "ADD_STA update");
	exp_mac_listener(&e.mac, IWX_FW_CTXT_ACTION_MODIFY, 1);
	expect_payload(b + 9, &e.mac, sizeof(e.mac), "MAC modify assoc");
	exp_sf(&e.sf, IWX_SF_FULL_ON);
	expect_payload(b + 10, &e.sf, sizeof(e.sf), "SF full on");
	memset(e.raw, 0, sizeof(e.raw));
	e.raw[0] = 1;	/* filter_own */
	{
		struct iwx_mcast_filter_cmd *mc = (void *)e.raw;
		mc->filter_own = 1;
		mc->pass_all = 1;
	}
	expect_payload(b + 11, e.raw,
	    roundup(sizeof(struct iwx_mcast_filter_cmd), 4), "MCAST");

	iwx_get_info(sc, &info);
	CHECK_EQ(info.state, IWX_STATE_RADIO_ON);
	CHECK_EQ(info.radio_channel, 6);
	CHECK_EQ(info.radio_mode, IWX_RADIO_MODE_LISTENER);
	CHECK_EQ(g_sim.txq[1].enabled, 1);
	CHECK_EQ(g_sim.txq[1].sta_mask, 1u << IWX_MONITOR_STA_ID);

	/* second start while running */
	CHECK_EQ(iwx_radio_start(sc, &rp), EBUSY);

	b = g_sim.ncmds;
	CHECK_EQ(iwx_radio_stop(sc), 0);
	if (!expect_seq(b, m2_down, (int)nitems(m2_down), "M2 stop"))
		return;
	exp_drain(&e.sta, IWX_MONITOR_STA_ID, 1);
	expect_payload(b + 0, &e.sta, sizeof(e.sta), "drain on");
	exp_flush(&e.flush, IWX_MONITOR_STA_ID);
	expect_payload(b + 1, &e.flush, sizeof(e.flush), "flush sta 2");
	exp_drain(&e.sta, IWX_MONITOR_STA_ID, 0);
	expect_payload(b + 2, &e.sta, sizeof(e.sta), "drain off");
	exp_sf(&e.sf, IWX_SF_INIT_OFF);
	expect_payload(b + 3, &e.sf, sizeof(e.sf), "SF init off");
	memset(&e.bf, 0, sizeof(e.bf));
	expect_payload(b + 4, &e.bf, sizeof(e.bf), "beacon filter off");
	exp_mac_listener(&e.mac, IWX_FW_CTXT_ACTION_MODIFY, 0);
	expect_payload(b + 5, &e.mac, sizeof(e.mac), "MAC modify unassoc");
	exp_scd_rm(&e.scd, IWX_MONITOR_STA_ID, IWX_MGMT_TID);
	expect_payload(b + 9, &e.scd, sizeof(e.scd), "SCD remove");
	memset(&e.rm, 0, sizeof(e.rm));
	e.rm.sta_id = IWX_MONITOR_STA_ID;
	expect_payload(b + 10, &e.rm, sizeof(e.rm), "REMOVE_STA");
	exp_binding(&e.bind, IWX_FW_CTXT_ACTION_REMOVE);
	expect_payload(b + 11, &e.bind, sizeof(e.bind), "BINDING remove");
	exp_mac_listener(&e.mac, IWX_FW_CTXT_ACTION_REMOVE, 0);
	expect_payload(b + 12, &e.mac, sizeof(e.mac), "MAC remove");
	exp_phy(&e.phy, IWX_FW_CTXT_ACTION_REMOVE, 6);
	expect_payload(b + 13, &e.phy, sizeof(e.phy), "PHY remove");

	iwx_get_info(sc, &info);
	CHECK_EQ(info.state, IWX_STATE_FW_READY);
	CHECK_EQ(info.radio_mode, IWX_RADIO_MODE_OFF);
	CHECK_EQ(g_sim.txq[1].enabled, 0);
	CHECK_EQ(sc->sc_flags & (IWX_FLAG_PHY_ACTIVE | IWX_FLAG_MAC_ACTIVE |
	    IWX_FLAG_BINDING_ACTIVE | IWX_FLAG_STA_ACTIVE), 0);

	/* idempotent stop: nothing sent */
	b = g_sim.ncmds;
	CHECK_EQ(iwx_radio_stop(sc), 0);
	CHECK_EQ(g_sim.ncmds, b);
}

/* one RX MPDU with the v3 descriptor, FCS appended */
static void
rx_one(const uint8_t *frame, uint32_t len, uint32_t status, uint32_t rnf,
    uint16_t phy_info, uint8_t mflg1, uint8_t mflg2)
{
	struct iwx_rx_mpdu_desc d;
	uint8_t buf[2600];

	memset(&d, 0, sizeof(d));
	d.mpdu_len = (uint16_t)(len + 4);
	d.mac_flags1 = mflg1;
	d.mac_flags2 = mflg2;
	d.phy_info = phy_info;
	d.status = status;
	d.v3.rate_n_flags = rnf;
	d.v3.energy_a = 40;
	d.v3.energy_b = 55;
	d.v3.channel = 6;
	d.v3.mac_context = 0;
	d.v3.gp2_on_air_rise = 0x11223344;
	d.v3.tsf_on_air_rise0 = 0x55667788;
	d.v3.tsf_on_air_rise1 = 0x00000099;
	memcpy(buf, frame, len);
	buf[len] = 0xde; buf[len + 1] = 0xad; buf[len + 2] = 0xbe;
	buf[len + 3] = 0xef;	/* FCS */
	sim_rx_mpdu(&d, buf, len + 4);
	sim_service_interrupts();
}

#define RX_OK	(IWX_RX_MPDU_RES_STATUS_CRC_OK | \
		 IWX_RX_MPDU_RES_STATUS_OVERRUN_OK)

static void
check_m2_rx(struct iwx_softc *sc)
{
	struct iwx_radio_params rp;
	struct iwx_info info;
	uint8_t action[40], qos[40];
	int n, i;

	for (i = 0; i < (int)sizeof(action); i++)
		action[i] = (uint8_t)i;
	action[0] = 0xd0;		/* action frame */
	action[1] = 0x00;

	/* not started: dropped silently */
	n = g_sim.rx_frames;
	rx_one(action, sizeof(action), RX_OK, 0, 0, 0, 0);
	CHECK_EQ(g_sim.rx_frames, n);

	memset(&rp, 0, sizeof(rp));
	rp.channel = 6;
	rp.flags = IWX_RADIO_F_LISTENER_ONLY;
	CHECK_EQ(iwx_radio_start(sc, &rp), 0);
	CHECK_EQ(sc->sc_rate_n_flags_version, 2);

	/* OFDM 6 Mb/s action frame: FCS stripped, metadata */
	rx_one(action, sizeof(action), RX_OK,
	    IWX_RATE_MCS_LEGACY_OFDM_MSK | 0 | IWX_RATE_MCS_ANT_A_MSK, 0, 0, 0);
	CHECK_EQ(g_sim.rx_frames, n + 1);
	CHECK_EQ(g_sim.last_frame_len, sizeof(action));
	CHECK(memcmp(g_sim.last_frame, action, sizeof(action)) == 0);
	CHECK_EQ(g_sim.last_meta.orig_len, sizeof(action) + 4);
	CHECK_EQ(g_sim.last_meta.channel, 6);
	CHECK_EQ((int)g_sim.last_meta.rssi_dbm, -40);
	CHECK_EQ(g_sim.last_meta.encoding, IWX_RXMETA_ENC_OFDM);
	CHECK_EQ(g_sim.last_meta.legacy_rate, 12);
	CHECK_EQ(g_sim.last_meta.mcs, 0xff);
	CHECK_EQ(g_sim.last_meta.gp2, 0x11223344);
	CHECK_EQ(g_sim.last_meta.tsf, 0x0000009955667788ull);
	CHECK_EQ(g_sim.last_meta.flags,
	    IWX_RXMETA_F_FCS_STRIPPED | IWX_RXMETA_F_TSF_VALID);

	/* CCK 11 Mb/s short preamble; TSF overloaded -> not valid */
	rx_one(action, sizeof(action), RX_OK,
	    IWX_RATE_MCS_CCK_MSK | 3, IWX_RX_MPDU_PHY_SHORT_PREAMBLE |
	    IWX_RX_MPDU_PHY_TSF_OVERLOAD, 0, 0);
	CHECK_EQ(g_sim.last_meta.encoding, IWX_RXMETA_ENC_CCK);
	CHECK_EQ(g_sim.last_meta.legacy_rate, 22);
	CHECK_EQ(g_sim.last_meta.flags,
	    IWX_RXMETA_F_FCS_STRIPPED | IWX_RXMETA_F_SHORTPRE);
	/* bit 7 on OFDM is NCCK_ADDTL_NTFY, not short preamble */
	rx_one(action, sizeof(action), RX_OK,
	    IWX_RATE_MCS_LEGACY_OFDM_MSK | 7, IWX_RX_MPDU_PHY_NCCK_ADDTL_NTFY,
	    0, 0);
	CHECK_EQ(g_sim.last_meta.legacy_rate, 108);
	CHECK_EQ(g_sim.last_meta.flags & IWX_RXMETA_F_SHORTPRE, 0);

	/* HT MCS 13 (NSS 2, code 5), 40 MHz, SGI, LDPC, in an A-MPDU */
	rx_one(action, sizeof(action), RX_OK,
	    IWX_RATE_MCS_HT_MSK | 5 | IWX_RATE_MCS_NSS_MSK |
	    IWX_RATE_MCS_CHAN_WIDTH_40 | IWX_RATE_MCS_SGI_MSK |
	    IWX_RATE_MCS_LDPC_MSK, IWX_RX_MPDU_PHY_AMPDU, 0, 0);
	CHECK_EQ(g_sim.last_meta.encoding, IWX_RXMETA_ENC_HT);
	CHECK_EQ(g_sim.last_meta.mcs, 13);
	CHECK_EQ(g_sim.last_meta.nss, 2);
	CHECK_EQ(g_sim.last_meta.bw, 1);
	CHECK(g_sim.last_meta.flags & IWX_RXMETA_F_SGI);
	CHECK(g_sim.last_meta.flags & IWX_RXMETA_F_LDPC);
	CHECK(g_sim.last_meta.flags & IWX_RXMETA_F_AMPDU);
	i = g_sim.last_meta.ampdu_ref;
	CHECK(i != 0);
	rx_one(action, sizeof(action), RX_OK, IWX_RATE_MCS_HT_MSK,
	    IWX_RX_MPDU_PHY_AMPDU, 0, 0);
	CHECK_EQ(g_sim.last_meta.ampdu_ref, i);		/* same A-MPDU */
	rx_one(action, sizeof(action), RX_OK, IWX_RATE_MCS_HT_MSK,
	    IWX_RX_MPDU_PHY_AMPDU | IWX_RX_MPDU_PHY_AMPDU_TOGGLE, 0, 0);
	CHECK_EQ(g_sim.last_meta.ampdu_ref, i + 1);	/* next one */
	/* VHT MCS 9 NSS 2 */
	rx_one(action, sizeof(action), RX_OK,
	    IWX_RATE_MCS_VHT_MSK | 9 | IWX_RATE_MCS_NSS_MSK, 0, 0, 0);
	CHECK_EQ(g_sim.last_meta.encoding, IWX_RXMETA_ENC_VHT);
	CHECK_EQ(g_sim.last_meta.mcs, 9);
	CHECK_EQ(g_sim.last_meta.nss, 2);

	/* rate_n_flags v1 decoding */
	sc->sc_rate_n_flags_version = 1;
	rx_one(action, sizeof(action), RX_OK,
	    IWX_RATE_MCS_HT_MSK_V1 | 0x0f | IWX_RATE_MCS_SGI_MSK_V1, 0, 0, 0);
	CHECK_EQ(g_sim.last_meta.encoding, IWX_RXMETA_ENC_HT);
	CHECK_EQ(g_sim.last_meta.mcs, 15);
	CHECK_EQ(g_sim.last_meta.nss, 2);
	CHECK(g_sim.last_meta.flags & IWX_RXMETA_F_SGI);
	rx_one(action, sizeof(action), RX_OK, IWX_RATE_MCS_CCK_MSK_V1 | 110,
	    0, 0, 0);
	CHECK_EQ(g_sim.last_meta.encoding, IWX_RXMETA_ENC_CCK);
	CHECK_EQ(g_sim.last_meta.legacy_rate, 22);
	rx_one(action, sizeof(action), RX_OK, 0x5, 0, 0, 0);
	CHECK_EQ(g_sim.last_meta.encoding, IWX_RXMETA_ENC_OFDM);
	CHECK_EQ(g_sim.last_meta.legacy_rate, 24);
	sc->sc_rate_n_flags_version = 2;

	/* QoS data with firmware pad after the 26-byte header */
	memset(qos, 0, sizeof(qos));
	qos[0] = 0x88;
	for (i = 2; i < 26; i++)
		qos[i] = (uint8_t)(0x40 + i);
	qos[26] = qos[27] = 0xaa;		/* pad */
	for (i = 28; i < (int)sizeof(qos); i++)
		qos[i] = (uint8_t)i;
	rx_one(qos, sizeof(qos), RX_OK, IWX_RATE_MCS_LEGACY_OFDM_MSK, 0, 0,
	    IWX_RX_MPDU_MFLG2_PAD);
	CHECK_EQ(g_sim.last_frame_len, sizeof(qos) - 2);
	CHECK(memcmp(g_sim.last_frame, qos, 26) == 0);
	CHECK(memcmp(g_sim.last_frame + 26, qos + 28, sizeof(qos) - 28) == 0);

	/* ACK (10 bytes) is delivered, 9 bytes are not */
	{
		uint8_t ack[10] = { 0xd4, 0, 0, 0, 1, 2, 3, 4, 5, 6 };

		n = g_sim.rx_frames;
		rx_one(ack, sizeof(ack), RX_OK, IWX_RATE_MCS_LEGACY_OFDM_MSK, 0,
		    0, 0);
		CHECK_EQ(g_sim.rx_frames, n + 1);
		CHECK_EQ(g_sim.last_frame_len, 10);
		rx_one(ack, 6, RX_OK, 0, 0, 0, 0);	/* 6 + FCS = 10 */
		CHECK_EQ(g_sim.rx_frames, n + 1);
	}

	/* bad CRC: counted and dropped */
	iwx_get_info(sc, &info);
	i = (int)info.rx_bad_fcs;
	n = g_sim.rx_frames;
	rx_one(action, sizeof(action), IWX_RX_MPDU_RES_STATUS_OVERRUN_OK, 0,
	    0, 0, 0);
	CHECK_EQ(g_sim.rx_frames, n);
	iwx_get_info(sc, &info);
	CHECK_EQ(info.rx_bad_fcs, (uint32_t)i + 1);

	CHECK_EQ(iwx_radio_stop(sc), 0);
	n = g_sim.rx_frames;
	rx_one(action, sizeof(action), RX_OK, 0, 0, 0, 0);
	CHECK_EQ(g_sim.rx_frames, n);		/* stopped: dropped */

	/* KEEP_BAD_FCS: delivered and flagged */
	rp.flags = IWX_RADIO_F_LISTENER_ONLY | IWX_RADIO_F_KEEP_BAD_FCS;
	CHECK_EQ(iwx_radio_start(sc, &rp), 0);
	rx_one(action, sizeof(action), IWX_RX_MPDU_RES_STATUS_CRC_OK, 0, 0, 0,
	    0);
	CHECK_EQ(g_sim.rx_frames, n + 1);
	CHECK(g_sim.last_meta.flags & IWX_RXMETA_F_BAD_FCS);
	CHECK(g_sim.last_meta.flags & IWX_RXMETA_F_FCS_STRIPPED);
	CHECK_EQ(iwx_radio_stop(sc), 0);

	/* passive channel 12: listener allowed, IBSS refused */
	rp.channel = 12;
	rp.flags = IWX_RADIO_F_LISTENER_ONLY;
	CHECK_EQ(iwx_radio_start(sc, &rp), 0);
	CHECK_EQ(iwx_radio_stop(sc), 0);
	rp.flags = 0;
	memcpy(rp.bssid, owly_bssid, 6);
	CHECK_EQ(iwx_radio_start(sc, &rp), EINVAL);
	rp.channel = 14;			/* invalid in the NVM */
	rp.flags = IWX_RADIO_F_LISTENER_ONLY;
	CHECK_EQ(iwx_radio_start(sc, &rp), EINVAL);
}

/* ---- M3 --------------------------------------------------------------- */

static const struct seq m3_up[] = {
	L(IWX_PHY_CONTEXT_CMD), D(IWX_RLC_CONFIG_CMD),
	L(IWX_MAC_CONTEXT_CMD), L(IWX_BEACON_TEMPLATE_CMD),
	L(IWX_BINDING_CONTEXT_CMD),
	L(IWX_ADD_STA), D(IWX_SCD_QUEUE_CONFIG_CMD),
	L(IWX_ADD_STA), D(IWX_SCD_QUEUE_CONFIG_CMD),
	L(IWX_REPLY_SF_CFG_CMD),
};
static const struct seq m3_down[] = {
	L(IWX_TXPATH_FLUSH), D(IWX_SCD_QUEUE_CONFIG_CMD), L(IWX_REMOVE_STA),
	L(IWX_TXPATH_FLUSH), D(IWX_SCD_QUEUE_CONFIG_CMD), L(IWX_REMOVE_STA),
	L(IWX_BINDING_CONTEXT_CMD), L(IWX_MAC_CONTEXT_CMD),
	L(IWX_PHY_CONTEXT_CMD),
};

static int
start_ibss(struct iwx_softc *sc, uint32_t flags)
{
	struct iwx_radio_params rp;

	memset(&rp, 0, sizeof(rp));
	rp.channel = 6;
	rp.flags = flags;
	memcpy(rp.bssid, owly_bssid, 6);
	return iwx_radio_start(sc, &rp);
}

static void
check_m3_sequences(struct iwx_softc *sc)
{
	static const uint8_t mc[6] = { 3, 0, 0, 0, 0, 0 };
	static const uint8_t bc[6] = { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
	union {
		struct iwx_phy_context_cmd_uhb phy;
		struct iwx_rlc_config_cmd rlc;
		struct iwx_mac_ctx_cmd mac;
		struct iwx_binding_cmd bind;
		struct iwx_add_sta_cmd sta;
		struct iwx_scd_queue_cfg_cmd scd;
		struct iwx_sf_cfg_cmd sf;
		struct iwx_tx_path_flush_cmd flush;
		struct iwx_rm_sta_cmd rm;
		uint8_t raw[512];
	} e;
	struct iwx_info info;
	size_t blen;
	int b;

	b = g_sim.ncmds;
	CHECK_EQ(start_ibss(sc, 0), 0);
	if (!expect_seq(b, m3_up, (int)nitems(m3_up), "M3 start"))
		return;
	exp_phy(&e.phy, IWX_FW_CTXT_ACTION_ADD, 6);
	expect_payload(b + 0, &e.phy, sizeof(e.phy), "IBSS PHY add");
	exp_rlc(&e.rlc, 2);
	expect_payload(b + 1, &e.rlc, sizeof(e.rlc), "IBSS RLC");
	exp_mac_ibss(&e.mac, IWX_FW_CTXT_ACTION_ADD, 1);
	expect_payload(b + 2, &e.mac, sizeof(e.mac), "IBSS MAC add");
	blen = exp_beacon(e.raw, 6);
	expect_payload(b + 3, e.raw, blen, "BEACON_TEMPLATE");
	CHECK_EQ(blen, 32 + 59);
	exp_binding(&e.bind, IWX_FW_CTXT_ACTION_ADD);
	expect_payload(b + 4, &e.bind, sizeof(e.bind), "IBSS BINDING");
	exp_add_sta(&e.sta, IWX_IBSS_MCAST_STA_ID, IWX_STA_MULTICAST, mc, 0, 0);
	expect_payload(b + 5, &e.sta, sizeof(e.sta), "ADD_STA mcast");
	exp_scd_add(&e.scd, sc, IWX_IBSS_CAB_QUEUE, IWX_IBSS_MCAST_STA_ID, 0);
	expect_payload(b + 6, &e.scd, sizeof(e.scd), "SCD add cab");
	exp_add_sta(&e.sta, IWX_IBSS_BCAST_STA_ID, IWX_STA_GENERAL_PURPOSE, bc,
	    0, 0);
	expect_payload(b + 7, &e.sta, sizeof(e.sta), "ADD_STA bcast");
	exp_scd_add(&e.scd, sc, IWX_IBSS_TX_QUEUE, IWX_IBSS_BCAST_STA_ID,
	    IWX_MGMT_TID);
	expect_payload(b + 8, &e.scd, sizeof(e.scd), "SCD add tx");
	exp_sf(&e.sf, IWX_SF_INIT_OFF);
	expect_payload(b + 9, &e.sf, sizeof(e.sf), "IBSS SF");

	iwx_get_info(sc, &info);
	CHECK_EQ(info.radio_mode, IWX_RADIO_MODE_IBSS);
	CHECK_EQ(g_sim.txq[IWX_IBSS_CAB_QUEUE].enabled, 1);
	CHECK_EQ(g_sim.txq[IWX_IBSS_TX_QUEUE].enabled, 1);
	CHECK_EQ(sc->sc_rx_fcs_included, 1);

	b = g_sim.ncmds;
	CHECK_EQ(iwx_radio_stop(sc), 0);
	if (!expect_seq(b, m3_down, (int)nitems(m3_down), "M3 stop"))
		return;
	exp_flush(&e.flush, IWX_IBSS_MCAST_STA_ID);
	expect_payload(b + 0, &e.flush, sizeof(e.flush), "flush mcast");
	exp_scd_rm(&e.scd, IWX_IBSS_MCAST_STA_ID, 0);
	expect_payload(b + 1, &e.scd, sizeof(e.scd), "SCD rm cab");
	memset(&e.rm, 0, sizeof(e.rm));
	e.rm.sta_id = IWX_IBSS_MCAST_STA_ID;
	expect_payload(b + 2, &e.rm, sizeof(e.rm), "REMOVE_STA mcast");
	exp_flush(&e.flush, IWX_IBSS_BCAST_STA_ID);
	expect_payload(b + 3, &e.flush, sizeof(e.flush), "flush bcast");
	exp_scd_rm(&e.scd, IWX_IBSS_BCAST_STA_ID, IWX_MGMT_TID);
	expect_payload(b + 4, &e.scd, sizeof(e.scd), "SCD rm tx");
	memset(&e.rm, 0, sizeof(e.rm));
	e.rm.sta_id = IWX_IBSS_BCAST_STA_ID;
	expect_payload(b + 5, &e.rm, sizeof(e.rm), "REMOVE_STA bcast");
	exp_binding(&e.bind, IWX_FW_CTXT_ACTION_REMOVE);
	expect_payload(b + 6, &e.bind, sizeof(e.bind), "IBSS BINDING rm");
	exp_mac_ibss(&e.mac, IWX_FW_CTXT_ACTION_REMOVE, 1);
	expect_payload(b + 7, &e.mac, sizeof(e.mac), "IBSS MAC rm");
	exp_phy(&e.phy, IWX_FW_CTXT_ACTION_REMOVE, 6);
	expect_payload(b + 8, &e.phy, sizeof(e.phy), "IBSS PHY rm");
	CHECK_EQ(g_sim.txq[IWX_IBSS_CAB_QUEUE].enabled, 0);
	CHECK_EQ(g_sim.txq[IWX_IBSS_TX_QUEUE].enabled, 0);

	/* NO_PROMISC: Linux IBSS filter only, FCS not included */
	b = g_sim.ncmds;
	CHECK_EQ(start_ibss(sc, IWX_RADIO_F_NO_PROMISC), 0);
	exp_mac_ibss(&e.mac, IWX_FW_CTXT_ACTION_ADD, 0);
	expect_payload(b + 2, &e.mac, sizeof(e.mac), "IBSS MAC no promisc");
	CHECK_EQ(sc->sc_rx_fcs_included, 0);
	CHECK_EQ(iwx_radio_stop(sc), 0);

	/* bad BSSIDs */
	{
		struct iwx_radio_params rp;

		memset(&rp, 0, sizeof(rp));
		rp.channel = 6;
		CHECK_EQ(iwx_radio_start(sc, &rp), EINVAL);	/* zero */
		memset(rp.bssid, 0xff, 6);
		CHECK_EQ(iwx_radio_start(sc, &rp), EINVAL);	/* group */
	}
}

/* ---- injection -------------------------------------------------------- */

static uint32_t
tx_one(struct iwx_softc *sc, const uint8_t *f, uint32_t len, uint32_t rate,
    uint32_t flags, uint32_t cookie, int *err)
{
	struct iwx_tx_params p;
	uint32_t txr = 0xdead;

	memset(&p, 0, sizeof(p));
	p.rate = rate;
	p.flags = flags;
	p.cookie = cookie;
	*err = iwx_tx_inject(sc, f, len, &p, &txr);
	return txr;
}

static void
mk_mgmt(uint8_t *f, uint32_t len, int bcast)
{
	uint32_t i;

	memset(f, 0, len);
	f[0] = 0xd0;		/* action */
	if (bcast)
		memset(f + 4, 0xff, 6);
	else {
		f[4] = 0x02; f[5] = 0x11; f[6] = 0x22;
		f[7] = 0x33; f[8] = 0x44; f[9] = 0x55;
	}
	memcpy(f + 10, g_sim.mac, 6);
	memcpy(f + 16, owly_bssid, 6);
	for (i = 24; i < len; i++)
		f[i] = (uint8_t)(i * 7);
}

static void
check_m3_tx(struct iwx_softc *sc)
{
	uint8_t f[2346], q[64];
	const struct sim_tx *t;
	struct iwx_info info;
	uint32_t txr, rnf, oa;
	int err, i, n0, s0;
	uint16_t len16;

	/* not started */
	mk_mgmt(f, 60, 1);
	tx_one(sc, f, 60, 12, 0, 1, &err);
	CHECK_EQ(err, ENXIO);

	CHECK_EQ(start_ibss(sc, 0), 0);
	CHECK_EQ(sc->sc_tx_qid, IWX_IBSS_TX_QUEUE);

	/* 6 Mb/s broadcast action frame */
	n0 = g_sim.ntx;
	s0 = g_sim.ntxst;
	txr = tx_one(sc, f, 60, 12, IWX_TX_F_NOACK, 0x1001, &err);
	CHECK_EQ(err, 0);
	CHECK_EQ(txr, 0);		/* group addressed: NOACK implicit */
	CHECK_EQ(g_sim.ntx, n0 + 1);
	t = &g_sim.tx[n0];
	CHECK_EQ(t->bad, 0);
	CHECK_EQ(t->qid, IWX_IBSS_TX_QUEUE);
	CHECK_EQ(t->idx, 0);
	CHECK_EQ(t->num_tbs, 3);
	CHECK_EQ(t->tb_len[1], 4 + 28 + 24 - IWX_FIRST_TB_SIZE);
	CHECK_EQ(t->tb_len[2], 60 - 24);
	CHECK_EQ(t->frame_len, 60);
	CHECK(memcmp(t->frame, f, 60) == 0);
	memcpy(&len16, &t->cmd[0], 2);
	CHECK_EQ(len16, 60);
	memcpy(&len16, &t->cmd[2], 2);
	CHECK_EQ(len16, IWX_TX_FLAGS_CMD_RATE | IWX_TX_FLAGS_ENCRYPT_DIS);
	memcpy(&oa, &t->cmd[4], 4);
	CHECK_EQ(oa, IWX_TX_CMD_OFFLD_MH_SIZE(12));
	memcpy(&rnf, &t->cmd[16], 4);
	CHECK_EQ(rnf, IWX_RATE_MCS_LEGACY_OFDM_MSK | IWX_RATE_MCS_ANT_A_MSK);
	CHECK_EQ(t->bc_ent, 60 | (0 << 14));	/* 2 + 3*10 bytes: 1 chunk */
	/* doorbell: qid << 16 | cur_hw */
	CHECK_EQ(*(uint32_t *)&g_sim.regs[IWX_HBUS_TARG_WRPTR],
	    (IWX_IBSS_TX_QUEUE << 16) | 1);
	iwx_get_info(sc, &info);
	CHECK_EQ(info.tx_pending, 1);
	sim_service_interrupts();
	CHECK_EQ(g_sim.ntxst, s0 + 1);
	CHECK_EQ(g_sim.txst[s0].cookie, 0x1001);
	CHECK_EQ(g_sim.txst[s0].result, IWX_TXST_SUCCESS);
	CHECK_EQ(g_sim.txst[s0].fw_status, IWX_TX_STATUS_SUCCESS);
	CHECK_EQ(g_sim.txst[s0].rate_n_flags, rnf);
	iwx_get_info(sc, &info);
	CHECK_EQ(info.tx_pending, 0);
	CHECK_EQ(info.tx_success, 1);
	CHECK_EQ(info.tx_submitted, 1);

	/* header only (no payload TB), HT MCS 7 SGI, unicast mgmt NOACK */
	mk_mgmt(f, 24, 0);
	n0 = g_sim.ntx;
	txr = tx_one(sc, f, 24, 7, IWX_TX_F_RATE_HT | IWX_TX_F_SGI |
	    IWX_TX_F_NOACK, 0x1002, &err);
	CHECK_EQ(err, 0);
	CHECK_EQ(txr, IWX_TXR_F_NOACK_NOT_HONORED);
	t = &g_sim.tx[n0];
	CHECK_EQ(t->bad, 0);
	CHECK_EQ(t->num_tbs, 2);
	CHECK_EQ(t->frame_len, 24);
	memcpy(&rnf, &t->cmd[16], 4);
	CHECK_EQ(rnf, IWX_RATE_MCS_HT_MSK | 7 | IWX_RATE_MCS_SGI_MSK |
	    IWX_RATE_MCS_ANT_A_MSK);
	sim_service_interrupts();
	CHECK_EQ(g_sim.txst[g_sim.ntxst - 1].flags,
	    IWX_TXR_F_NOACK_NOT_HONORED);

	/* MCS 9 = 2 streams, antennas A+B */
	n0 = g_sim.ntx;
	tx_one(sc, f, 24, 9, IWX_TX_F_RATE_HT, 0x1003, &err);
	CHECK_EQ(err, 0);
	memcpy(&rnf, &g_sim.tx[n0].cmd[16], 4);
	CHECK_EQ(rnf, IWX_RATE_MCS_HT_MSK | 1 | IWX_RATE_MCS_NSS_MSK |
	    IWX_RATE_MCS_ANT_AB_MSK);
	/* CCK 1 Mb/s */
	n0 = g_sim.ntx;
	tx_one(sc, f, 24, 2, 0, 0x1004, &err);
	CHECK_EQ(err, 0);
	memcpy(&rnf, &g_sim.tx[n0].cmd[16], 4);
	CHECK_EQ(rnf, IWX_RATE_MCS_CCK_MSK | 0 | IWX_RATE_MCS_ANT_A_MSK);
	sim_service_interrupts();

	/* invalid requests */
	tx_one(sc, f, 24, 16, IWX_TX_F_RATE_HT, 1, &err);
	CHECK_EQ(err, EINVAL);
	tx_one(sc, f, 24, 13, 0, 1, &err);		/* no such legacy rate */
	CHECK_EQ(err, EINVAL);
	tx_one(sc, f, 24, 12, IWX_TX_F_SGI, 1, &err);
	CHECK_EQ(err, EINVAL);
	tx_one(sc, f, 9, 12, 0, 1, &err);
	CHECK_EQ(err, EINVAL);
	tx_one(sc, f, 2347, 12, 0, 1, &err);
	CHECK_EQ(err, EINVAL);
	f[0] |= 0x01;					/* protocol version 1 */
	tx_one(sc, f, 24, 12, 0, 1, &err);
	CHECK_EQ(err, EINVAL);
	f[0] = 0xd0;
	mk_mgmt(f, 24, 0);
	f[1] = 0x80;					/* +HTC: 28-byte header */
	tx_one(sc, f, 26, 12, 0, 1, &err);
	CHECK_EQ(err, EINVAL);

	/* QoS data, unicast, NOACK -> Ack Policy = No Ack, header pad */
	memset(q, 0, sizeof(q));
	q[0] = 0x88; q[1] = 0x00;
	q[4] = 0x02; q[5] = 0x99;
	memcpy(q + 10, g_sim.mac, 6);
	memcpy(q + 16, owly_bssid, 6);
	q[24] = 0x05;				/* TID 5, normal ack */
	for (i = 26; i < (int)sizeof(q); i++)
		q[i] = (uint8_t)i;
	n0 = g_sim.ntx;
	txr = tx_one(sc, q, sizeof(q), 24, IWX_TX_F_NOACK |
	    IWX_TX_F_ASSIGN_SEQ, 0x2000, &err);
	CHECK_EQ(err, 0);
	CHECK_EQ(txr, IWX_TXR_F_ACK_POLICY_SET | IWX_TXR_F_SEQ_ASSIGNED);
	t = &g_sim.tx[n0];
	CHECK_EQ(t->bad, 0);
	memcpy(&oa, &t->cmd[4], 4);
	CHECK_EQ(oa, IWX_TX_CMD_OFFLD_MH_SIZE(13) | IWX_TX_CMD_OFFLD_PAD);
	CHECK_EQ(t->tb_len[1], 4 + 28 + 26 + 2 - IWX_FIRST_TB_SIZE);
	CHECK_EQ(t->frame[24], 0x25);		/* TID 5 | No Ack */
	CHECK_EQ(t->frame[22] | (t->frame[23] << 8), 0 << 4);
	CHECK(memcmp(t->frame + 26, q + 26, sizeof(q) - 26) == 0);
	/* sequence numbers advance */
	tx_one(sc, q, sizeof(q), 24, IWX_TX_F_ASSIGN_SEQ, 0x2001, &err);
	CHECK_EQ(err, 0);
	t = &g_sim.tx[n0 + 1];
	CHECK_EQ(t->frame[22] | (t->frame[23] << 8), 1 << 4);
	CHECK_EQ(t->frame[24], 0x05);		/* untouched without NOACK */
	sim_service_interrupts();
	CHECK_EQ(g_sim.txst[g_sim.ntxst - 1].seq_ctl, 1 << 4);

	/* failure with retries */
	g_sim.tx_status = 0x83;			/* TX_STATUS_FAIL_LONG_LIMIT */
	g_sim.tx_failure_frame = 15;
	g_sim.tx_failure_rts = 2;
	mk_mgmt(f, 100, 0);
	tx_one(sc, f, 100, 12, 0, 0x3000, &err);
	CHECK_EQ(err, 0);
	sim_service_interrupts();
	CHECK_EQ(g_sim.txst[g_sim.ntxst - 1].cookie, 0x3000);
	CHECK_EQ(g_sim.txst[g_sim.ntxst - 1].result, IWX_TXST_FAILED);
	CHECK_EQ(g_sim.txst[g_sim.ntxst - 1].retries, 15);
	CHECK_EQ(g_sim.txst[g_sim.ntxst - 1].rts_retries, 2);
	CHECK_EQ(g_sim.txst[g_sim.ntxst - 1].fw_status, 0x83);
	g_sim.tx_status = IWX_TX_STATUS_SUCCESS;
	g_sim.tx_failure_frame = g_sim.tx_failure_rts = 0;

	/* fire-and-forget: no status callback */
	s0 = g_sim.ntxst;
	tx_one(sc, f, 100, 12, 0, IWX_TX_COOKIE_NONE, &err);
	CHECK_EQ(err, 0);
	sim_service_interrupts();
	CHECK_EQ(g_sim.ntxst, s0);

	/* a later SSN frees frames without their own response */
	g_sim.tx_hold = 1;
	s0 = g_sim.ntxst;
	for (i = 0; i < 3; i++) {
		tx_one(sc, f, 100, 12, 0, 0x4000 + (uint32_t)i, &err);
		CHECK_EQ(err, 0);
	}
	g_sim.tx_hold = 0;
	tx_one(sc, f, 100, 12, 0, 0x4003, &err);
	sim_service_interrupts();
	CHECK_EQ(g_sim.ntxst, s0 + 4);
	CHECK_EQ(g_sim.txst[s0].cookie, 0x4003);
	CHECK_EQ(g_sim.txst[s0].result, IWX_TXST_SUCCESS);
	for (i = 1; i < 4; i++) {
		CHECK_EQ(g_sim.txst[s0 + i].cookie, 0x4000 + (uint32_t)i - 1);
		CHECK_EQ(g_sim.txst[s0 + i].result, IWX_TXST_NO_STATUS);
	}
	iwx_get_info(sc, &info);
	CHECK_EQ(info.tx_pending, 0);

	/* ring wrap: 300 frames one by one, payload slots recycled */
	s0 = g_sim.ntxst;
	g_sim.ntx = 0;
	for (i = 0; i < 300; i++) {
		mk_mgmt(f, 64 + (uint32_t)i, 0);
		tx_one(sc, f, 64 + (uint32_t)i, 12, 0, 0x5000 + (uint32_t)i,
		    &err);
		if (err)
			break;
		sim_service_interrupts();
	}
	CHECK_EQ(i, 300);
	CHECK_EQ(g_sim.ntxst, s0 + 300);
	CHECK_EQ(g_sim.txst[s0 + 299].cookie, 0x5000 + 299);
	CHECK_EQ(g_sim.tx_errors, 0);

	/* 64 in flight, the 65th is refused */
	g_sim.tx_hold = 1;
	g_sim.ntx = 0;
	s0 = g_sim.ntxst;
	for (i = 0; i < IWX_TX_MAX_INFLIGHT; i++) {
		mk_mgmt(f, 2346, 0);
		f[100] = (uint8_t)i;
		tx_one(sc, f, 2346, 12, 0, 0x6000 + (uint32_t)i, &err);
		CHECK_EQ(err, 0);
	}
	tx_one(sc, f, 100, 12, 0, 0x6fff, &err);
	CHECK_EQ(err, ENOBUFS);
	iwx_get_info(sc, &info);
	CHECK_EQ(info.tx_pending, IWX_TX_MAX_INFLIGHT);
	CHECK_EQ(g_sim.tx_errors, 0);
	/* each of the 64 held frames kept its own payload slot */
	{
		int ok = 1;
		for (i = 0; i < IWX_TX_MAX_INFLIGHT && i < (int)nitems(g_sim.tx);
		    i++) {
			t = &g_sim.tx[g_sim.ntx - IWX_TX_MAX_INFLIGHT + i];
			if (t->frame_len != 2346 || t->frame[100] != (uint8_t)i)
				ok = 0;
		}
		CHECK(ok);
	}

	/* stop: flush reports every held frame exactly once */
	CHECK_EQ(iwx_radio_stop(sc), 0);
	CHECK_EQ(g_sim.ntxst, s0 + IWX_TX_MAX_INFLIGHT);
	{
		int ok = 1;
		for (i = 0; i < IWX_TX_MAX_INFLIGHT; i++)
			if (g_sim.txst[s0 + i].cookie != 0x6000 + (uint32_t)i ||
			    g_sim.txst[s0 + i].result != IWX_TXST_FLUSHED)
				ok = 0;
		CHECK(ok);
	}
	g_sim.tx_hold = 0;
	iwx_get_info(sc, &info);
	CHECK_EQ(info.tx_pending, 0);
	CHECK_EQ(info.tx_flushed, IWX_TX_MAX_INFLIGHT + 3);

	/* listener mode injects on queue 1 too (monitor station) */
	{
		struct iwx_radio_params rp;

		memset(&rp, 0, sizeof(rp));
		rp.channel = 1;
		rp.flags = IWX_RADIO_F_LISTENER_ONLY;
		CHECK_EQ(iwx_radio_start(sc, &rp), 0);
		n0 = g_sim.ntx;
		s0 = g_sim.ntxst;
		mk_mgmt(f, 50, 1);
		tx_one(sc, f, 50, 12, 0, 0x7000, &err);
		CHECK_EQ(err, 0);
		sim_service_interrupts();
		CHECK_EQ(g_sim.tx[n0].qid, IWX_DQA_INJECT_MONITOR_QUEUE);
		CHECK_EQ(g_sim.ntxst, s0 + 1);
		CHECK_EQ(iwx_radio_stop(sc), 0);
	}
	CHECK_EQ(g_sim.tx_errors, 0);
}

/* ---- failures, restart, surprise removal ------------------------------- */

static void
check_failures(const uint8_t *fw, size_t fwlen)
{
	struct iwx_softc *sc;
	struct iwx_info info;
	uint8_t f[100];
	int err, i, s0;

	/* ADD_STA refused: start fails, contexts are torn down again */
	sc = radio_attach(fw, fwlen);
	if (sc == NULL)
		return;
	g_sim.add_sta_status = 0;
	CHECK_EQ(start_ibss(sc, 0), EIO);
	iwx_get_info(sc, &info);
	CHECK_EQ(info.state, IWX_STATE_FW_READY);
	CHECK_EQ(sc->sc_flags & (IWX_FLAG_PHY_ACTIVE | IWX_FLAG_MAC_ACTIVE |
	    IWX_FLAG_BINDING_ACTIVE | IWX_FLAG_STA_ACTIVE), 0);
	g_sim.add_sta_status = IWX_ADD_STA_SUCCESS;
	CHECK_EQ(start_ibss(sc, 0), 0);		/* and works afterwards */
	CHECK_EQ(iwx_radio_stop(sc), 0);

	/* firmware hands back another queue id */
	g_sim.scd_wrong_qid = 1;
	CHECK_EQ(start_ibss(sc, 0), EIO);
	g_sim.scd_wrong_qid = 0;
	iwx_get_info(sc, &info);
	CHECK_EQ(info.state, IWX_STATE_FW_READY);
	CHECK_EQ(g_sim.txq[IWX_IBSS_CAB_QUEUE].enabled, 0);

	/* binding status != 0 */
	g_sim.binding_status = 1;
	CHECK_EQ(start_ibss(sc, IWX_RADIO_F_LISTENER_ONLY), EIO);
	g_sim.binding_status = 0;

	/* start / stop / restart across modes */
	for (i = 0; i < 3; i++) {
		CHECK_EQ(start_ibss(sc, 0), 0);
		CHECK_EQ(iwx_radio_stop(sc), 0);
		CHECK_EQ(start_ibss(sc, IWX_RADIO_F_LISTENER_ONLY), 0);
		CHECK_EQ(iwx_radio_stop(sc), 0);
	}
	CHECK_EQ(g_sim.txq[1].adds, g_sim.txq[1].removes);
	CHECK_EQ(g_sim.txq[2].adds, g_sim.txq[2].removes);

	/* teardown failure -> device stopped, frames flushed */
	CHECK_EQ(start_ibss(sc, 0), 0);
	g_sim.tx_hold = 1;
	s0 = g_sim.ntxst;
	mk_mgmt(f, sizeof(f), 1);
	for (i = 0; i < 5; i++) {
		struct iwx_tx_params p = { 12, 0, 0x100 + (uint32_t)i };
		CHECK_EQ(iwx_tx_inject(sc, f, sizeof(f), &p, NULL), 0);
	}
	g_sim.fail_cmd_group = IWX_LONG_GROUP;
	g_sim.fail_cmd_opcode = IWX_BINDING_CONTEXT_CMD;
	err = iwx_radio_stop(sc);
	CHECK(err != 0);
	g_sim.fail_cmd_group = g_sim.fail_cmd_opcode = -1;
	iwx_get_info(sc, &info);
	CHECK_EQ(info.state, IWX_STATE_DOWN);
	CHECK_EQ(g_sim.ntxst, s0 + 5);
	for (i = 0; i < 5; i++)
		CHECK_EQ(g_sim.txst[s0 + i].result, IWX_TXST_FLUSHED);
	g_sim.tx_hold = 0;
	/* device comes back */
	CHECK_EQ(iwx_init(sc), 0);
	CHECK_EQ(start_ibss(sc, 0), 0);
	CHECK_EQ(iwx_radio_stop(sc), 0);
	iwx_stop(sc);
	radio_detach(sc);

	/* firmware error while the radio is on */
	sc = radio_attach(fw, fwlen);
	if (sc == NULL)
		return;
	CHECK_EQ(start_ibss(sc, 0), 0);
	g_sim.tx_hold = 1;
	s0 = g_sim.ntxst;
	{
		struct iwx_tx_params p = { 12, 0, 0x200 };
		CHECK_EQ(iwx_tx_inject(sc, f, sizeof(f), &p, NULL), 0);
	}
	sim_raise(0, IWX_MSIX_HW_INT_CAUSES_REG_SW_ERR);
	sim_service_interrupts();
	CHECK(g_sim.fatal_count > 0);
	{
		struct iwx_tx_params p = { 12, 0, 0x201 };
		CHECK_EQ(iwx_tx_inject(sc, f, sizeof(f), &p, NULL), EIO);
	}
	CHECK(iwx_radio_stop(sc) != 0);		/* EIO -> iwx_stop */
	CHECK_EQ(g_sim.ntxst, s0 + 1);
	CHECK_EQ(g_sim.txst[s0].result, IWX_TXST_FLUSHED);
	g_sim.tx_hold = 0;
	iwx_stop(sc);
	radio_detach(sc);

	/* surprise removal during RX and TX */
	sc = radio_attach(fw, fwlen);
	if (sc == NULL)
		return;
	CHECK_EQ(start_ibss(sc, 0), 0);
	g_sim.tx_hold = 1;
	s0 = g_sim.ntxst;
	for (i = 0; i < 4; i++) {
		struct iwx_tx_params p = { 24, 0, 0x300 + (uint32_t)i };
		CHECK_EQ(iwx_tx_inject(sc, f, sizeof(f), &p, NULL), 0);
	}
	g_sim.gone = 1;
	/* interrupt storm of all-ones while gone */
	sim_raise(IWX_MSIX_FH_INT_CAUSES_Q0, 0);
	{
		uint32_t fh = 0, hw = 0;
		iwx_isr_msix(sc, &fh, &hw);
		iwx_dpc_msix(sc, fh, hw);
	}
	g_sim.irq_pending = 0;
	i = 4;
	{
		struct iwx_tx_params p = { 24, 0, 0x3ff };
		err = iwx_tx_inject(sc, f, sizeof(f), &p, NULL);
		CHECK(err == EIO || err == 0);
		if (err == 0)
			i++;	/* accepted before the error was noticed */
	}
	CHECK(iwx_radio_stop(sc) != 0);		/* commands time out */
	iwx_get_info(sc, &info);
	CHECK(info.state == IWX_STATE_DOWN || info.state == IWX_STATE_ERROR);
	CHECK_EQ(g_sim.ntxst, s0 + i);
	{
		int ok = 1;
		for (i = 0; i < g_sim.ntxst - s0; i++)
			if (g_sim.txst[s0 + i].result != IWX_TXST_FLUSHED)
				ok = 0;
		CHECK(ok);
	}
	CHECK_EQ(iwx_radio_stop(sc), 0);	/* idempotent after removal */
	iwx_stop(sc);
	g_sim.gone = 0;
	radio_detach(sc);
}

void
test_radio(const uint8_t *fw, size_t fwlen)
{
	struct iwx_softc *sc;

	printf("test_radio (M2/M3, real firmware image)\n");
	sc = radio_attach(fw, fwlen);
	if (sc == NULL)
		return;
	check_m2_sequences(sc);
	check_m2_rx(sc);
	check_m3_sequences(sc);
	check_m3_tx(sc);
	CHECK_EQ(g_sim.tfd_errors, 0);
	CHECK_EQ(g_sim.tx_errors, 0);
	iwx_stop(sc);
	radio_detach(sc);

	check_failures(fw, fwlen);
}
