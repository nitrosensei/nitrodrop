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

/* Mock osdep + AX210-family device model, see mock.h. */

#include <stdarg.h>
#include <malloc.h>
#include "mock.h"

struct sim g_sim;

#define UMAC(a)	(((a) + 0x300000u) & 0x00ffffffu)

static uint32_t
reg_get(uint32_t off)
{
	uint32_t v;

	if (off + 4 > sizeof(g_sim.regs))
		return 0;
	memcpy(&v, &g_sim.regs[off], 4);
	return v;
}

static void
reg_set(uint32_t off, uint32_t v)
{
	if (off + 4 <= sizeof(g_sim.regs))
		memcpy(&g_sim.regs[off], &v, 4);
}

static uint32_t
prph_get(uint32_t a)
{
	int i;

	if (a == UMAC(IWX_RFH_GEN_STATUS_GEN3))
		return IWX_RXF_DMA_IDLE;
	for (i = 0; i < g_sim.nprph; i++)
		if (g_sim.prph_addr[i] == a)
			return g_sim.prph_val[i];
	return 0;
}

static void sim_boot(void);
static void sim_process_cmds(uint32_t wrptr);

static void
prph_set(uint32_t a, uint32_t v)
{
	int i;

	for (i = 0; i < g_sim.nprph; i++)
		if (g_sim.prph_addr[i] == a)
			break;
	if (i == g_sim.nprph && g_sim.nprph < (int)nitems(g_sim.prph_addr))
		g_sim.nprph++;
	if (i < (int)nitems(g_sim.prph_addr)) {
		g_sim.prph_addr[i] = a;
		g_sim.prph_val[i] = v;
	}

	if (a == UMAC(IWX_UREG_CPU_INIT_RUN) && v == 1)
		sim_boot();
	if (a == UMAC(IWX_UREG_DOORBELL_TO_ISR6) &&
	    (v & IWX_UREG_DOORBELL_TO_ISR6_PNVM)) {
		uint32_t zero = 0;
		g_sim.pnvm_doorbells++;
		sim_rx_packet(IWX_REGULATORY_AND_NVM_GROUP,
		    IWX_PNVM_INIT_COMPLETE, 0x80, 0, &zero, sizeof(zero));
	}
}

void
sim_reset(void)
{
	memset(&g_sim, 0, sizeof(g_sim));
	g_sim.alive_ver = 6;
	g_sim.alive_status = IWX_ALIVE_STATUS_OK;
	g_sim.sku_id[0] = 0x00000101;
	g_sim.nvm_v4 = 1;
	g_sim.mcc_v4 = 1;
	g_sim.lar_enabled = 1;
	g_sim.fail_cmd_group = g_sim.fail_cmd_opcode = -1;
	g_sim.mac[0] = 0x8c; g_sim.mac[1] = 0x1d; g_sim.mac[2] = 0x96;
	g_sim.mac[3] = 0x12; g_sim.mac[4] = 0x34; g_sim.mac[5] = 0x56;
	g_sim.quiet = 1;

	reg_set(IWX_CSR_HW_REV, 0x00000371);		/* So, B step */
	reg_set(IWX_CSR_HW_RF_ID, 0x0010a000);		/* Hr2 */
	/* MAC address straps (iwx_flip_hw_address byte order) */
	reg_set(0x380 + 0x08, ((uint32_t)g_sim.mac[0] << 24) |
	    ((uint32_t)g_sim.mac[1] << 16) | ((uint32_t)g_sim.mac[2] << 8) |
	    g_sim.mac[3]);
	reg_set(0x380 + 0x0c, ((uint32_t)g_sim.mac[4] << 8) | g_sim.mac[5]);
	sim_set_pci_ids(0x51f1, 0x0274);
}

void
sim_set_pci_ids(uint16_t dev, uint16_t sub)
{
	g_sim.pci_cfg[0x00 / 4] = ((uint32_t)dev << 16) | 0x8086;
	g_sim.pci_cfg[0x04 / 4] = 0x00100006;	/* cap list, MEM, BM */
	g_sim.pci_cfg[0x2c / 4] = ((uint32_t)sub << 16) | 0x8086;
	g_sim.pci_cfg[0x34 / 4] = 0x40;
	g_sim.pci_cfg[0x40 / 4] = 0x00020010;	/* PCIe cap, next = 0 */
	g_sim.pci_cfg[(0x40 + 0x10) / 4] = 0x00000002;	/* ASPM L1 */
	g_sim.pci_cfg[(0x40 + 0x28) / 4] = 0x00000400;	/* LTR enabled */
}

void
sim_raise(uint32_t fh, uint32_t hw)
{
	g_sim.fh_cause |= fh;
	g_sim.hw_cause |= hw;
	g_sim.irq_pending = 1;
}

void
sim_service_interrupts(void)
{
	struct iwx_softc *sc = g_sim.sc;
	int guard = 0;

	while (sc != NULL && g_sim.irq_pending && guard++ < 1000) {
		uint32_t fh, hw;
		uint32_t fhm = reg_get(IWX_CSR_MSIX_FH_INT_MASK_AD);
		uint32_t hwm = reg_get(IWX_CSR_MSIX_HW_INT_MASK_AD);

		if (!sc->sc_msix)
			return;		/* MSI/INTx path is not modelled */
		/* a masked cause stays latched until it is unmasked */
		if ((g_sim.fh_cause & ~fhm) == 0 && (g_sim.hw_cause & ~hwm) == 0)
			return;
		g_sim.irq_pending = 0;
		if (g_sim.lock_depth != 0)
			g_sim.lock_errors++;
		iwx_isr_msix(sc, &fh, &hw);
		iwx_dpc_msix(sc, fh, hw);
		g_sim.intr_delivered++;
		if (g_sim.fh_cause || g_sim.hw_cause)
			g_sim.irq_pending = 1;
	}
}

void
sim_rx_packet(uint8_t group, uint8_t code, uint8_t qid, uint8_t idx,
    const void *payload, uint32_t paylen)
{
	struct iwx_softc *sc = g_sim.sc;
	struct iwx_rx_transfer_desc *fd;
	struct iwx_rx_completion_desc *cd;
	struct iwx_rx_packet *pkt;
	uint16_t *status;
	uint8_t *buf;
	int i;

	if (sc == NULL || sc->rxq.free_desc_dma.vaddr == NULL)
		return;
	/* the free RBD table the device was told about at boot */
	fd = (struct iwx_rx_transfer_desc *)(uintptr_t)
	    (g_sim.scratch_ctrl.rbd_cfg.free_rbd_addr ?
	    g_sim.scratch_ctrl.rbd_cfg.free_rbd_addr :
	    sc->rxq.free_desc_dma.paddr);
	i = g_sim.rx_write % IWX_RX_MQ_RING_COUNT;
	if (fd[i].rbid != i || fd[i].addr == 0) {
		printf("  sim: bad free RBD %d (rbid %u addr 0x%llx)\n", i,
		    fd[i].rbid, (unsigned long long)fd[i].addr);
		g_sim.tfd_errors++;
		return;
	}
	buf = (uint8_t *)(uintptr_t)fd[i].addr;
	memset(buf, 0, 64);
	pkt = (struct iwx_rx_packet *)buf;
	pkt->len_n_flags = htole32(sizeof(pkt->hdr) + paylen);
	pkt->hdr.code = code;
	pkt->hdr.flags = group;
	pkt->hdr.idx = idx;
	pkt->hdr.qid = qid;
	if (paylen)
		memcpy(pkt->data, payload, paylen);
	/* invalid marker after the packet, like real RBs */
	memset(buf + roundup(sizeof(*pkt) + paylen, IWX_FH_RSCSR_FRAME_ALIGN),
	    0, 16);

	cd = sc->rxq.used_desc_dma.vaddr;
	cd[i].rbid = htole16((uint16_t)i);

	g_sim.rx_write++;
	status = sc->rxq.stat_dma.vaddr;
	*status = htole16((uint16_t)(g_sim.rx_write & 0xfff));
	sim_raise(IWX_MSIX_FH_INT_CAUSES_Q0, 0);
}

static void
ctxt_fail(const char *msg)
{
	if (g_sim.ctxt_ok) {
		g_sim.ctxt_ok = 0;
		snprintf(g_sim.ctxt_err, sizeof(g_sim.ctxt_err), "%s", msg);
	}
}

/* Firmware self-load triggered by UREG_CPU_INIT_RUN. */
static void
sim_boot(void)
{
	struct iwx_softc *sc = g_sim.sc;
	const struct iwx_context_info_gen3 *ci;
	const struct iwx_prph_scratch *ps;
	const struct iwx_fw_sects *fws;
	uint64_t addr, iml;
	uint32_t imlsz, cf;
	int lmac;

	g_sim.ctxt_ok = 1;
	g_sim.ctxt_err[0] = '\0';

	addr = reg_get(IWX_CSR_CTXT_INFO_ADDR) |
	    ((uint64_t)reg_get(IWX_CSR_CTXT_INFO_ADDR + 4) << 32);
	iml = reg_get(IWX_CSR_IML_DATA_ADDR) |
	    ((uint64_t)reg_get(IWX_CSR_IML_DATA_ADDR + 4) << 32);
	imlsz = reg_get(IWX_CSR_IML_SIZE_ADDR);
	if (addr == 0 || sc == NULL) {
		ctxt_fail("context info address not programmed");
		return;
	}
	if (!(reg_get(IWX_CSR_CTXT_INFO_BOOT_CTRL) & IWX_CSR_AUTO_FUNC_BOOT_ENA))
		ctxt_fail("AUTO_FUNC_BOOT_ENA not set");

	ci = (const void *)(uintptr_t)addr;
	g_sim.ctxt = *ci;
	ps = (const void *)(uintptr_t)ci->prph_scratch_base_addr;
	if (ps == NULL) {
		ctxt_fail("no prph scratch");
		return;
	}
	g_sim.scratch_ctrl = ps->ctrl_cfg;

	if (ci->prph_scratch_size != sizeof(struct iwx_prph_scratch))
		ctxt_fail("prph_scratch_size");
	if (ci->prph_info_base_addr != sc->prph_info_dma.paddr)
		ctxt_fail("prph_info_base_addr");
	if (ci->cr_head_idx_arr_base_addr != sc->rxq.stat_dma.paddr)
		ctxt_fail("cr_head_idx_arr_base_addr");
	if (ci->tr_tail_idx_arr_base_addr != sc->prph_info_dma.paddr + 2048)
		ctxt_fail("tr_tail_idx_arr_base_addr");
	if (ci->cr_tail_idx_arr_base_addr != sc->prph_info_dma.paddr + 3072)
		ctxt_fail("cr_tail_idx_arr_base_addr");
	if (ci->mtr_base_addr != sc->txq[0].desc_dma.paddr)
		ctxt_fail("mtr_base_addr");
	if (ci->mcr_base_addr != sc->rxq.used_desc_dma.paddr)
		ctxt_fail("mcr_base_addr");
	if (ci->mtr_size != 5)		/* log2(256) - 3 */
		ctxt_fail("mtr_size");
	if (ci->mcr_size != 9)		/* log2(512) */
		ctxt_fail("mcr_size");

	cf = ps->ctrl_cfg.control.control_flags;
	if (cf != (IWX_PRPH_SCRATCH_RB_SIZE_4K | IWX_PRPH_SCRATCH_MTR_MODE |
	    (IWX_PRPH_MTR_FORMAT_256B & IWX_PRPH_SCRATCH_MTR_FORMAT) |
	    IWX_PRPH_SCRATCH_IMR_DEBUG_EN))
		ctxt_fail("control_flags");
	if (ps->ctrl_cfg.version.mac_id != (uint16_t)reg_get(IWX_CSR_HW_REV))
		ctxt_fail("version.mac_id");
	if (ps->ctrl_cfg.version.size != sizeof(struct iwx_prph_scratch) / 4)
		ctxt_fail("version.size");
	if (ps->ctrl_cfg.rbd_cfg.free_rbd_addr != sc->rxq.free_desc_dma.paddr)
		ctxt_fail("free_rbd_addr");

	if (iml == 0 || imlsz != sc->sc_fw.iml_len ||
	    memcmp((const void *)(uintptr_t)iml, sc->sc_fw.iml, imlsz) != 0)
		ctxt_fail("IML address/size/content");

	/* every firmware section copied to its own DMA buffer */
	fws = &sc->sc_fw.fw_sects[IWX_UCODE_TYPE_REGULAR];
	lmac = iwx_get_num_sections(fws, 0);
	g_sim.lmac0 = ps->dram.lmac_img[0];
	g_sim.umac0 = ps->dram.umac_img[0];
	g_sim.paging0 = ps->dram.virtual_img[0];
	if (lmac > 0 && (g_sim.lmac0 == 0 ||
	    memcmp((const void *)(uintptr_t)g_sim.lmac0,
	    fws->fw_sect[0].fws_data, fws->fw_sect[0].fws_len) != 0))
		ctxt_fail("lmac_img[0] content");
	if (fws->fw_count > lmac + 1 && (g_sim.umac0 == 0 ||
	    memcmp((const void *)(uintptr_t)g_sim.umac0,
	    fws->fw_sect[lmac + 1].fws_data,
	    fws->fw_sect[lmac + 1].fws_len) != 0))
		ctxt_fail("umac_img[0] content");

	g_sim.booted = 1;
	g_sim.boots++;
	g_sim.cmd_read = 0;
	g_sim.rx_write = 0;

	/* ALIVE notification (+ ALIVE interrupt cause) */
	{
		struct iwx_alive_resp_v6 a6;
		struct iwx_alive_resp_v5 a5;
		struct iwx_alive_resp_v4 a4;

		memset(&a6, 0, sizeof(a6));
		memset(&a5, 0, sizeof(a5));
		memset(&a4, 0, sizeof(a4));
		a6.status = a5.status = a4.status = (uint16_t)g_sim.alive_status;
		a6.lmac_data[0].dbg_ptrs.error_event_table_ptr = 0x800000;
		a5.lmac_data[0].dbg_ptrs.error_event_table_ptr = 0x800000;
		a4.lmac_data[0].dbg_ptrs.error_event_table_ptr = 0x800000;
		a6.umac_data.dbg_ptrs.error_info_addr = 0x801000;
		a5.umac_data.dbg_ptrs.error_info_addr = 0x801000;
		a4.umac_data.dbg_ptrs.error_info_addr = 0x801000;
		memcpy(a6.sku_id.data, g_sim.sku_id, sizeof(g_sim.sku_id));
		memcpy(a5.sku_id.data, g_sim.sku_id, sizeof(g_sim.sku_id));
		if (g_sim.alive_ver >= 6)
			sim_rx_packet(IWX_LEGACY_GROUP, IWX_ALIVE, 0x80, 0,
			    &a6, sizeof(a6));
		else if (g_sim.alive_ver == 5)
			sim_rx_packet(IWX_LEGACY_GROUP, IWX_ALIVE, 0x80, 0,
			    &a5, sizeof(a5));
		else
			sim_rx_packet(IWX_LEGACY_GROUP, IWX_ALIVE, 0x80, 0,
			    &a4, sizeof(a4));
		sim_raise(0, IWX_MSIX_HW_INT_CAUSES_REG_ALIVE);
	}
}

static uint32_t
sim_chan_flags(int idx)
{
	/* 2.4 GHz 1-13 valid (1-11 active), 14 invalid; 5 GHz up to 165 */
	if (idx < 14)
		return idx < 13 ? (IWX_NVM_CHANNEL_VALID |
		    (idx < 11 ? IWX_NVM_CHANNEL_ACTIVE : 0)) : 0;
	if (idx < 14 + 33)
		return IWX_NVM_CHANNEL_VALID | IWX_NVM_CHANNEL_ACTIVE;
	return 0;
}

static void
sim_reply(int idx, uint8_t group, uint8_t opcode, int failed,
    const void *payload, uint32_t len)
{
	sim_rx_packet((uint8_t)(group | (failed ? IWX_CMD_FAILED_MSK : 0)),
	    opcode, 0 /* cmd queue */, (uint8_t)idx, payload, len);
}

static void
sim_handle_cmd(int idx, const struct iwx_cmd_header_wide *hdr,
    const uint8_t *payload)
{
	static uint8_t rsp[2048];
	struct iwx_cmd_response generic;
	int failed;

	failed = (hdr->group_id == g_sim.fail_cmd_group &&
	    hdr->opcode == g_sim.fail_cmd_opcode);
	if (g_sim.drop_responses)
		return;

	memset(&generic, 0, sizeof(generic));
	memset(rsp, 0, sizeof(rsp));

	if (hdr->group_id == IWX_REGULATORY_AND_NVM_GROUP &&
	    hdr->opcode == IWX_NVM_GET_INFO) {
		struct iwx_nvm_get_info_rsp *r = (void *)rsp;
		struct iwx_nvm_get_info_rsp_v3 *r3 = (void *)rsp;
		int i;

		/* general/mac_sku/phy_sku/lar are at the same offsets in v3 */
		r->general.nvm_version = 0x0a1d;
		r->general.n_hw_addrs = 1;
		r->mac_sku.mac_sku_flags =
		    IWX_NVM_MAC_SKU_FLAGS_BAND_2_4_ENABLED |
		    IWX_NVM_MAC_SKU_FLAGS_BAND_5_2_ENABLED |
		    IWX_NVM_MAC_SKU_FLAGS_802_11N_ENABLED |
		    IWX_NVM_MAC_SKU_FLAGS_802_11AC_ENABLED |
		    IWX_NVM_MAC_SKU_FLAGS_802_11AX_ENABLED;
		r->phy_sku.tx_chains = 3;
		r->phy_sku.rx_chains = 3;
		r->regulatory.lar_enabled = (uint32_t)g_sim.lar_enabled;
		if (g_sim.nvm_v4) {
			r->regulatory.n_channels = IWX_NUM_CHANNELS;
			for (i = 0; i < IWX_NUM_CHANNELS; i++)
				r->regulatory.channel_profile[i] =
				    sim_chan_flags(i);
			sim_reply(idx, hdr->group_id, hdr->opcode, failed,
			    r, sizeof(*r));
		} else {
			for (i = 0; i < IWX_NUM_CHANNELS_V1; i++)
				r3->regulatory.channel_profile[i] =
				    (uint16_t)sim_chan_flags(i);
			sim_reply(idx, hdr->group_id, hdr->opcode, failed,
			    r3, sizeof(*r3));
		}
		return;
	}

	if (hdr->group_id == IWX_LONG_GROUP &&
	    hdr->opcode == IWX_MCC_UPDATE_CMD) {
		struct iwx_mcc_update_resp_v4 *r = (void *)rsp;
		uint32_t n = 20, i;	/* fewer channels than the NVM */

		r->mcc = 0x5a5a;	/* "ZZ" */
		r->n_channels = n;
		for (i = 0; i < n; i++)
			r->channels[i] = sim_chan_flags((int)i);
		/* v3 and v4 response share this header + u32 channels */
		sim_reply(idx, hdr->group_id, hdr->opcode, failed, r,
		    (uint32_t)(sizeof(*r) + n * sizeof(uint32_t)));
		return;
	}

	sim_reply(idx, hdr->group_id, hdr->opcode, failed,
	    &generic, sizeof(generic));

	if (hdr->group_id == IWX_REGULATORY_AND_NVM_GROUP &&
	    hdr->opcode == IWX_NVM_ACCESS_COMPLETE)
		sim_rx_packet(IWX_LEGACY_GROUP, IWX_INIT_COMPLETE_NOTIF, 0x80,
		    0, &generic, sizeof(generic));
	(void)payload;
}

/* Doorbell on the command queue: consume TFDs up to the write pointer. */
static void
sim_process_cmds(uint32_t wrptr)
{
	struct iwx_tfh_tfd *tfds;
	uint32_t target = (wrptr & 0xffff) % IWX_TX_RING_COUNT;

	if (!g_sim.booted || g_sim.ctxt.mtr_base_addr == 0) {
		g_sim.tfd_errors++;
		return;
	}
	tfds = (struct iwx_tfh_tfd *)(uintptr_t)g_sim.ctxt.mtr_base_addr;

	while ((uint32_t)g_sim.cmd_read != target) {
		struct iwx_tfh_tfd *d = &tfds[g_sim.cmd_read];
		uint8_t buf[4096 + 64];
		uint64_t a0 = 0, a1 = 0;
		uint16_t l0, l1 = 0, ntb;
		const struct iwx_cmd_header_wide *hdr;
		struct sim_cmd *rc;
		uint32_t total;

		ntb = le16toh(d->num_tbs);
		memcpy(&a0, &d->tbs[0].addr, sizeof(a0));
		l0 = le16toh(d->tbs[0].tb_len);
		if (ntb == 2) {
			memcpy(&a1, &d->tbs[1].addr, sizeof(a1));
			l1 = le16toh(d->tbs[1].tb_len);
			if (l0 != IWX_FIRST_TB_SIZE || a1 != a0 + IWX_FIRST_TB_SIZE)
				g_sim.tfd_errors++;
		} else if (ntb != 1) {
			g_sim.tfd_errors++;
			ntb = 1;
		}
		total = (uint32_t)l0 + l1;
		if (a0 == 0 || total < sizeof(*hdr) || total > sizeof(buf)) {
			g_sim.tfd_errors++;
			g_sim.cmd_read = (g_sim.cmd_read + 1) % IWX_TX_RING_COUNT;
			continue;
		}
		memcpy(buf, (const void *)(uintptr_t)a0, l0);
		if (ntb == 2)
			memcpy(buf + l0, (const void *)(uintptr_t)a1, l1);
		hdr = (const void *)buf;
		if (hdr->qid != 0 || hdr->idx != g_sim.cmd_read ||
		    le16toh(hdr->length) != total - sizeof(*hdr))
			g_sim.tfd_errors++;

		if (g_sim.ncmds < SIM_MAX_CMDS) {
			rc = &g_sim.cmds[g_sim.ncmds++];
			rc->group = hdr->group_id;
			rc->opcode = hdr->opcode;
			rc->version = hdr->version;
			rc->idx = hdr->idx;
			rc->paylen = (uint16_t)(total - sizeof(*hdr));
			memcpy(rc->payload, buf + sizeof(*hdr),
			    MIN(sizeof(rc->payload), total - sizeof(*hdr)));
		}
		sim_handle_cmd(g_sim.cmd_read, hdr, buf + sizeof(*hdr));
		g_sim.cmd_read = (g_sim.cmd_read + 1) % IWX_TX_RING_COUNT;
	}
}

int
sim_find_cmd(uint8_t group, uint8_t opcode)
{
	int i;

	for (i = 0; i < g_sim.ncmds; i++)
		if (g_sim.cmds[i].group == group &&
		    g_sim.cmds[i].opcode == opcode)
			return i;
	return -1;
}

/* ---- osdep.h ------------------------------------------------------------ */

uint32_t
osdep_read32(void *os, uint32_t off)
{
	uint32_t v = reg_get(off);

	(void)os;
	switch (off) {
	case IWX_CSR_GP_CNTRL:
		v |= IWX_CSR_GP_CNTRL_REG_FLAG_HW_RF_KILL_SW;	/* radio on */
		if (v & (IWX_CSR_GP_CNTRL_REG_FLAG_INIT_DONE |
		    IWX_CSR_GP_CNTRL_REG_FLAG_MAC_ACCESS_REQ))
			v |= IWX_CSR_GP_CNTRL_REG_FLAG_MAC_CLOCK_READY;
		v &= ~IWX_CSR_GP_CNTRL_REG_FLAG_GOING_TO_SLEEP;
		break;
	case IWX_CSR_RESET:
		if (v & IWX_CSR_RESET_REG_FLAG_STOP_MASTER)
			v |= IWX_CSR_RESET_REG_FLAG_MASTER_DISABLED;
		break;
	case IWX_HBUS_TARG_PRPH_RDAT:
		v = prph_get(g_sim.prph_raddr);
		break;
	case IWX_HBUS_TARG_MEM_RDAT:
		v = 0;
		break;
	case IWX_CSR_MSIX_FH_INT_CAUSES_AD:
		v = g_sim.fh_cause;
		break;
	case IWX_CSR_MSIX_HW_INT_CAUSES_AD:
		v = g_sim.hw_cause;
		break;
	default:
		break;
	}
	return v;
}

void
osdep_write32(void *os, uint32_t off, uint32_t val)
{
	(void)os;
	switch (off) {
	case IWX_HBUS_TARG_PRPH_WADDR:
		g_sim.prph_waddr = val & 0x00ffffff;
		break;
	case IWX_HBUS_TARG_PRPH_RADDR:
		g_sim.prph_raddr = val & 0x00ffffff;
		break;
	case IWX_HBUS_TARG_PRPH_WDAT:
		prph_set(g_sim.prph_waddr, val);
		return;
	case IWX_HBUS_TARG_WRPTR:
		reg_set(off, val);
		if ((val >> 16) == IWX_DQA_CMD_QUEUE)
			sim_process_cmds(val);
		return;
	case IWX_CSR_MSIX_FH_INT_CAUSES_AD:
		g_sim.fh_cause &= ~val;		/* W1C */
		return;
	case IWX_CSR_MSIX_HW_INT_CAUSES_AD:
		g_sim.hw_cause &= ~val;
		return;
	case IWX_RFH_Q0_FRBDCB_WIDX_TRG:
		g_sim.last_frbdcb_widx = val;
		break;
	default:
		break;
	}
	reg_set(off, val);
}

void
osdep_write8(void *os, uint32_t off, uint8_t val)
{
	(void)os;
	if (off < sizeof(g_sim.regs))
		g_sim.regs[off] = val;
}

void osdep_barrier(void *os) { (void)os; }

uint32_t
osdep_pci_read32(void *os, uint32_t off)
{
	(void)os;
	return off / 4 < nitems(g_sim.pci_cfg) ? g_sim.pci_cfg[off / 4] : 0xffffffff;
}

void
osdep_pci_write32(void *os, uint32_t off, uint32_t val)
{
	(void)os;
	if (off / 4 < nitems(g_sim.pci_cfg))
		g_sim.pci_cfg[off / 4] = val;
}

void osdep_delay_us(uint32_t us) { g_sim.now_us += us; }
uint64_t osdep_time_us(void) { return g_sim.now_us; }

void *
osdep_malloc(size_t size)
{
	g_sim.mallocs++;
	return calloc(1, size ? size : 1);
}

void
osdep_free(void *p, size_t size)
{
	(void)size;
	if (p != NULL) {
		g_sim.frees++;
		free(p);
	}
}

int
osdep_dma_alloc(void *os, struct iwx_dma_info *dma, size_t size, size_t align)
{
	void *p;

	(void)os;
	if (align < 64)
		align = 64;
	p = _aligned_malloc(size, align);
	if (p == NULL)
		return ENOMEM;
	memset(p, 0, size);
	dma->vaddr = p;
	dma->paddr = (uint64_t)(uintptr_t)p;	/* identity "IOMMU" */
	dma->size = size;
	dma->os_handle = p;
	g_sim.dma_allocs++;
	return 0;
}

void
osdep_dma_free(void *os, struct iwx_dma_info *dma)
{
	(void)os;
	if (dma->os_handle != NULL) {
		_aligned_free(dma->os_handle);
		g_sim.dma_frees++;
	}
	memset(dma, 0, sizeof(*dma));
}

void
osdep_lock(void *os)
{
	(void)os;
	if (g_sim.lock_depth != 0)
		g_sim.lock_errors++;	/* would deadlock on a spinlock */
	g_sim.lock_depth++;
}

void
osdep_unlock(void *os)
{
	(void)os;
	if (g_sim.lock_depth <= 0)
		g_sim.lock_errors++;
	g_sim.lock_depth--;
}

void
osdep_event_signal(void *os, enum osdep_event_id ev)
{
	(void)os;
	g_sim.event_signaled[ev] = 1;
}

void
osdep_event_clear(void *os, enum osdep_event_id ev)
{
	(void)os;
	g_sim.event_signaled[ev] = 0;
}

int
osdep_event_wait(void *os, enum osdep_event_id ev, uint32_t timeout_ms)
{
	(void)os;
	if (g_sim.lock_depth != 0)
		g_sim.lock_errors++;	/* sleeping with a spinlock held */
	sim_service_interrupts();
	if (g_sim.event_signaled[ev])
		return 0;
	g_sim.now_us += (uint64_t)timeout_ms * 1000;
	return ETIMEDOUT;
}

void
osdep_log(void *os, enum osdep_log_level lvl, const char *fmt, ...)
{
	va_list ap;

	(void)os;
	if (g_sim.quiet && lvl != OSDEP_LOG_ERR)
		return;
	if (g_sim.quiet > 1)
		return;
	va_start(ap, fmt);
	printf("    [core] ");
	vprintf(fmt, ap);
	va_end(ap);
}

int
osdep_fw_load(void *os, const char *name, uint8_t **data, size_t *len)
{
	(void)os;
	(void)name;
	if (g_sim.fw_data == NULL)
		return ENOENT;
	*data = malloc(g_sim.fw_len);
	if (*data == NULL)
		return ENOMEM;
	memcpy(*data, g_sim.fw_data, g_sim.fw_len);
	*len = g_sim.fw_len;
	g_sim.fw_loads++;
	return 0;
}

void
osdep_fw_release(void *os, uint8_t *data, size_t len)
{
	(void)os;
	(void)len;
	free(data);
}

void
osdep_rx_frame(void *os, const struct iwx_rx_meta *meta,
    const uint8_t *frame, uint32_t len)
{
	(void)os;
	g_sim.rx_frames++;
	g_sim.last_meta = *meta;
	g_sim.last_frame_len = len;
	memcpy(g_sim.last_frame, frame, MIN(len, sizeof(g_sim.last_frame)));
}

void
osdep_fatal(void *os, enum osdep_fatal_reason reason)
{
	(void)os;
	g_sim.fatal_count++;
	g_sim.fatal_last = (int)reason;
}
