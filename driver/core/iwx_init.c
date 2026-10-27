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
 * airdrop-win port notes (driver/core/iwx_init.c)
 *
 * Attach, firmware bring-up and NVM, ported from OpenBSD if_iwx.c rev
 * 1.230: iwx_attach() (hardware part), iwx_find_device_cfg(),
 * iwx_preinit(), iwx_init()/iwx_stop() (hardware part), iwx_post_alive(),
 * iwx_load_pnvm(), iwx_load_ucode_wait_alive(), iwx_run_init_mvm_ucode(),
 * iwx_nvm_get() and helpers, iwx_init_channel_map(), iwx_init_hw() and the
 * commands it sends, iwx_nic_error()/iwx_nic_umac_error().
 *
 * The order of register accesses and host commands is iwx's, statement
 * by statement. Divergences (all intentional):
 *   D-IN1 iwx_attach(): only 8086:51F1 ("WL_22500_17", AX203 in So) is
 *         accepted and only the So/So-F + Hr rows of iwx_dev_info_table
 *         are kept, because only iwlwifi-so-a0-hr-b0-77 is shipped. If no
 *         row matches, attach fails instead of falling back to the
 *         So+GF firmware like iwx does.
 *   D-IN2 iwx_attach(): only the command queue is allocated (D-TR4);
 *         interrupts are connected by the OS backend, not here.
 *         The subsystem ID comes from the backend (PCI config space),
 *         iwx reads PCI_SUBSYS_ID_REG itself.
 *   D-IN3 iwx_init_hw(): iwx returns without iwx_nic_unlock() when
 *         BT_CONFIG, SOC_CONFIGURATION or DQA_ENABLE fail (lock leak,
 *         harmless there because iwx_stop_device() resets the counter).
 *         Here every failure path goes through the unlock.
 *   D-IN4 iwx_init_hw(): POWER_TABLE_CMD is sent with power level 0
 *         (device power save off). OpenBSD defaults to IEEE80211_F_PMGTON
 *         (level 3) for station mode; we never want power save on a
 *         fixed-channel monitor/injection radio. In OpenBSD monitor mode
 *         the command is not sent at all; it is sent here because our
 *         final opmode is IBSS-like (see M2 notes in iwx_radio.c).
 *   D-IN5 iwx_load_pnvm(): PNVM parsing (iwx_pnvm_parse) is not ported.
 *         So+Hr has no PNVM (cfg pnvm_name == NULL), so iwx only rings
 *         the UREG_DOORBELL_TO_ISR6 doorbell and waits - that part is
 *         ported. A device that needs a PNVM fails with ENOTSUP.
 *   D-IN6 iwx_nvm_get(): a missing response packet (firmware returned
 *         CMD_FAILED) yields EIO; iwx would dereference NULL.
 *   D-IN7 channel list is stored in sc_nvm.channels[] for user mode
 *         instead of net80211 ic_channels[]; the filtering rules (VALID
 *         flag, 5 GHz SKU bit, no 6 GHz) are iwx's.
 *   D-IN8 iwx_nic_error(): output goes to osdep_log; the error_id is kept
 *         in sc_last_fw_error for the GET_INFO ioctl.
 */

#include "iwx_var.h"

/* So and So-F with Hr rows of iwx_dev_info_table (same order as iwx). */
static const struct iwx_device_cfg iwx_cfg_so_a0_hr_b0 = {
	IWX_SO_A_HR_B_FW,	/* fw_name */
	NULL,			/* pnvm_name */
	0, 0, 0, 0
};

#define _IWX_DEV_INFO(_device, _subdevice, _mac_type, _mac_step, _rf_type, \
		      _rf_id, _no_160, _cores, _cdb, _jacket, _cfg)	   \
	{ (uint16_t)(_device), (uint16_t)(_subdevice), (uint16_t)(_mac_type), \
	  (uint16_t)(_rf_type), (uint8_t)(_mac_step), (uint8_t)(_rf_id),   \
	  (uint8_t)(_no_160), (uint8_t)(_cores), (uint8_t)(_cdb),	   \
	  (uint8_t)(_jacket), &(_cfg) }

static const struct iwx_dev_info iwx_dev_info_table[] = {
	/* So with Hr */
	_IWX_DEV_INFO(IWX_CFG_ANY, IWX_CFG_ANY,
		      IWX_CFG_MAC_TYPE_SO, IWX_CFG_ANY,
		      IWX_CFG_RF_TYPE_HR2, IWX_CFG_ANY,
		      IWX_CFG_NO_160, IWX_CFG_ANY, IWX_CFG_NO_CDB, IWX_CFG_ANY,
		      iwx_cfg_so_a0_hr_b0), /* AX203 */
	_IWX_DEV_INFO(IWX_CFG_ANY, IWX_CFG_ANY,
		      IWX_CFG_MAC_TYPE_SO, IWX_CFG_ANY,
		      IWX_CFG_RF_TYPE_HR1, IWX_CFG_ANY,
		      IWX_CFG_NO_160, IWX_CFG_ANY, IWX_CFG_NO_CDB, IWX_CFG_ANY,
		      iwx_cfg_so_a0_hr_b0), /* ax101 */
	_IWX_DEV_INFO(IWX_CFG_ANY, IWX_CFG_ANY,
		      IWX_CFG_MAC_TYPE_SO, IWX_CFG_ANY,
		      IWX_CFG_RF_TYPE_HR2, IWX_CFG_ANY,
		      IWX_CFG_160, IWX_CFG_ANY, IWX_CFG_NO_CDB, IWX_CFG_ANY,
		      iwx_cfg_so_a0_hr_b0), /* ax201 */

	/* So-F with Hr */
	_IWX_DEV_INFO(IWX_CFG_ANY, IWX_CFG_ANY,
		      IWX_CFG_MAC_TYPE_SOF, IWX_CFG_ANY,
		      IWX_CFG_RF_TYPE_HR2, IWX_CFG_ANY,
		      IWX_CFG_NO_160, IWX_CFG_ANY, IWX_CFG_NO_CDB, IWX_CFG_ANY,
		      iwx_cfg_so_a0_hr_b0), /* AX203 */
	_IWX_DEV_INFO(IWX_CFG_ANY, IWX_CFG_ANY,
		      IWX_CFG_MAC_TYPE_SOF, IWX_CFG_ANY,
		      IWX_CFG_RF_TYPE_HR1, IWX_CFG_ANY,
		      IWX_CFG_NO_160, IWX_CFG_ANY, IWX_CFG_NO_CDB, IWX_CFG_ANY,
		      iwx_cfg_so_a0_hr_b0), /* AX101 */
	_IWX_DEV_INFO(IWX_CFG_ANY, IWX_CFG_ANY,
		      IWX_CFG_MAC_TYPE_SOF, IWX_CFG_ANY,
		      IWX_CFG_RF_TYPE_HR2, IWX_CFG_ANY,
		      IWX_CFG_160, IWX_CFG_ANY, IWX_CFG_NO_CDB, IWX_CFG_ANY,
		      iwx_cfg_so_a0_hr_b0), /* AX201 */
};

/* PCI product IDs (OpenBSD pcidevs naming). */
#define PCI_PRODUCT_INTEL_WL_22500_17	0x51f1	/* Wi-Fi 6 AX201/AX203 (So) */

static const uint8_t iwx_nvm_channels_8000[] = {
	/* 2.4 GHz */
	1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
	/* 5 GHz */
	36, 40, 44, 48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92,
	96, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144,
	149, 153, 157, 161, 165, 169, 173, 177, 181
};

const struct iwx_device_cfg *
iwx_find_device_cfg(struct iwx_softc *sc)
{
	uint16_t sdev_id;
	uint16_t mac_type, rf_type;
	uint8_t mac_step, cdb, jacket, rf_id, no_160, cores;
	int i;

	sdev_id = sc->sc_sdev;
	mac_type = (uint16_t)IWX_CSR_HW_REV_TYPE(sc->sc_hw_rev);
	mac_step = (uint8_t)IWX_CSR_HW_REV_STEP(sc->sc_hw_rev << 2);
	rf_type = (uint16_t)IWX_CSR_HW_RFID_TYPE(sc->sc_hw_rf_id);
	cdb = (uint8_t)IWX_CSR_HW_RFID_IS_CDB(sc->sc_hw_rf_id);
	jacket = (uint8_t)IWX_CSR_HW_RFID_IS_JACKET(sc->sc_hw_rf_id);

	rf_id = (uint8_t)IWX_SUBDEVICE_RF_ID(sdev_id);
	no_160 = (uint8_t)IWX_SUBDEVICE_NO_160(sdev_id);
	cores = (uint8_t)IWX_SUBDEVICE_CORES(sdev_id);

	for (i = (int)nitems(iwx_dev_info_table) - 1; i >= 0; i--) {
		const struct iwx_dev_info *dev_info = &iwx_dev_info_table[i];

		if (dev_info->device != (uint16_t)IWX_CFG_ANY &&
		    dev_info->device != sc->sc_pid)
			continue;

		if (dev_info->subdevice != (uint16_t)IWX_CFG_ANY &&
		    dev_info->subdevice != sdev_id)
			continue;

		if (dev_info->mac_type != (uint16_t)IWX_CFG_ANY &&
		    dev_info->mac_type != mac_type)
			continue;

		if (dev_info->mac_step != (uint8_t)IWX_CFG_ANY &&
		    dev_info->mac_step != mac_step)
			continue;

		if (dev_info->rf_type != (uint16_t)IWX_CFG_ANY &&
		    dev_info->rf_type != rf_type)
			continue;

		if (dev_info->cdb != (uint8_t)IWX_CFG_ANY &&
		    dev_info->cdb != cdb)
			continue;

		if (dev_info->jacket != (uint8_t)IWX_CFG_ANY &&
		    dev_info->jacket != jacket)
			continue;

		if (dev_info->rf_id != (uint8_t)IWX_CFG_ANY &&
		    dev_info->rf_id != rf_id)
			continue;

		if (dev_info->no_160 != (uint8_t)IWX_CFG_ANY &&
		    dev_info->no_160 != no_160)
			continue;

		if (dev_info->cores != (uint8_t)IWX_CFG_ANY &&
		    dev_info->cores != cores)
			continue;

		return dev_info->cfg;
	}

	return NULL;
}

/* pci_get_capability(PCI_CAP_PCIEXPRESS) */
static int
iwx_find_pcie_cap(struct iwx_softc *sc)
{
	uint32_t reg;
	int off, n;

	reg = osdep_pci_read32(sc->sc_os, 0x04);	/* command/status */
	if (((reg >> 16) & 0x0010) == 0)		/* capability list */
		return 0;
	off = (int)(osdep_pci_read32(sc->sc_os, 0x34) & 0xfc);
	for (n = 0; off >= 0x40 && n < 48; n++) {
		reg = osdep_pci_read32(sc->sc_os, (uint32_t)off);
		if ((reg & 0xff) == 0x10)		/* PCI Express */
			return off;
		off = (int)((reg >> 8) & 0xfc);
	}
	return 0;
}

size_t
iwx_softc_size(void)
{
	return sizeof(struct iwx_softc);
}

void
iwx_set_msix(struct iwx_softc *sc, int msix)
{
	sc->sc_msix = msix ? 1 : 0;
}

int
iwx_attach(struct iwx_softc *sc, void *os, uint16_t pci_device,
    uint16_t pci_subdevice)
{
	const struct iwx_device_cfg *cfg;
	uint32_t reg;
	size_t ctxt_info_size;
	int err, i;

	memset(sc, 0, sizeof(*sc));
	sc->sc_os = os;
	sc->sc_pid = pci_device;
	sc->sc_sdev = pci_subdevice;
	sc->cmd_big_idx = -1;
	sc->sc_state = IWX_STATE_DOWN;

	sc->sc_cap_off = iwx_find_pcie_cap(sc);
	if (sc->sc_cap_off == 0) {
		IWX_ERR(sc, "PCIe capability structure not found!\n");
		return ENXIO;
	}

	/*
	 * We disable the RETRY_TIMEOUT register (0x41) to keep
	 * PCI Tx retries from interfering with C3 CPU state.
	 */
	reg = osdep_pci_read32(sc->sc_os, 0x40);
	osdep_pci_write32(sc->sc_os, 0x40, reg & ~0xff00u);

	/* Clear pending interrupts. */
	IWX_WRITE(sc, IWX_CSR_INT_MASK, 0);
	IWX_WRITE(sc, IWX_CSR_INT, ~0u);
	IWX_WRITE(sc, IWX_CSR_FH_INT_STATUS, ~0u);

	sc->sc_hw_rev = (int)IWX_READ(sc, IWX_CSR_HW_REV);
	sc->sc_hw_rf_id = (int)IWX_READ(sc, IWX_CSR_HW_RF_ID);
	if ((uint32_t)sc->sc_hw_rev == 0xffffffff) {
		IWX_ERR(sc, "device not responding (BAR0 reads all-ones)\n");
		return ENXIO;
	}

	/*
	 * In the 8000 HW family the format of the 4 bytes of CSR_HW_REV have
	 * changed, and now the revision step also includes bit 0-1 (no more
	 * "dash" value). To keep hw_rev backwards compatible - we'll store it
	 * in the old format.
	 */
	sc->sc_hw_rev = (sc->sc_hw_rev & 0xfff0) |
			(IWX_CSR_HW_REV_STEP(sc->sc_hw_rev << 2) << 2);

	switch (pci_device) {
	case PCI_PRODUCT_INTEL_WL_22500_17:
		sc->sc_fwname = NULL;	/* iwx: IWX_SO_A_GF_A_FW, see D-IN1 */
		sc->sc_pnvm_name = NULL;
		sc->sc_device_family = IWX_DEVICE_FAMILY_AX210;
		sc->sc_integrated = 1;
		sc->sc_ltr_delay = IWX_SOC_FLAGS_LTR_APPLY_DELAY_2500;
		sc->sc_low_latency_xtal = 1;
		sc->sc_xtal_latency = 12000;
		sc->sc_tx_with_siso_diversity = 0;
		sc->sc_uhb_supported = 0;
		sc->sc_imr_enabled = 1;
		break;
	default:
		IWX_ERR(sc, "unknown adapter type 0x%04x\n", pci_device);
		return ENXIO;
	}

	cfg = iwx_find_device_cfg(sc);
	if (cfg) {
		sc->sc_fwname = cfg->fw_name;
		sc->sc_pnvm_name = cfg->pnvm_name;
		sc->sc_tx_with_siso_diversity = cfg->tx_with_siso_diversity;
		sc->sc_uhb_supported = cfg->uhb_supported;
		if (cfg->xtal_latency) {
			sc->sc_xtal_latency = cfg->xtal_latency;
			sc->sc_low_latency_xtal = cfg->low_latency_xtal;
		}
	} else {
		IWX_ERR(sc, "unsupported MAC/RF combination: hw_rev 0x%x "
		    "rf_id 0x%x subdevice 0x%04x\n", sc->sc_hw_rev,
		    sc->sc_hw_rf_id, pci_subdevice);
		return ENOTSUP;
	}

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ)
		sc->mac_addr_from_csr = 0x30;
	else
		sc->mac_addr_from_csr = 0x380;
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		sc->sc_umac_prph_offset = 0x300000;
		sc->max_tfd_queue_size = IWX_TFD_QUEUE_SIZE_MAX_GEN3;
	} else
		sc->max_tfd_queue_size = IWX_TFD_QUEUE_SIZE_MAX;

	if (iwx_apm_init(sc) == 0 && iwx_nic_lock(sc)) {
		iwx_get_crf_id(sc);
		iwx_nic_unlock(sc);
		iwx_apm_stop(sc);
	}

	/* Allocate DMA memory for loading firmware. */
	ctxt_info_size = sizeof(struct iwx_context_info_gen3);
	err = iwx_dma_contig_alloc(sc, &sc->ctxt_info_dma, ctxt_info_size, 0);
	if (err) {
		IWX_ERR(sc, "could not allocate memory for loading firmware\n");
		goto fail;
	}

	err = iwx_dma_contig_alloc(sc, &sc->prph_scratch_dma,
	    sizeof(struct iwx_prph_scratch), 0);
	if (err) {
		IWX_ERR(sc, "could not allocate prph scratch memory\n");
		goto fail;
	}

	/*
	 * Allocate prph information. The driver doesn't use this.
	 * We use the second half of this page to give the device
	 * some dummy TR/CR tail pointers - which shouldn't be
	 * necessary as we don't use this, but the hardware still
	 * reads/writes there and we can't let it go do that with
	 * a NULL pointer.
	 */
	err = iwx_dma_contig_alloc(sc, &sc->prph_info_dma, IWX_PAGE_SIZE, 0);
	if (err) {
		IWX_ERR(sc, "could not allocate prph info memory\n");
		goto fail;
	}

	/* Allocate interrupt cause table (ICT).*/
	err = iwx_dma_contig_alloc(sc, &sc->ict_dma,
	    IWX_ICT_SIZE, 1 << IWX_ICT_PADDR_SHIFT);
	if (err) {
		IWX_ERR(sc, "could not allocate ICT table\n");
		goto fail;
	}

	/* D-TR4: command queue only */
	err = iwx_alloc_tx_ring(sc, &sc->txq[IWX_DQA_CMD_QUEUE],
	    IWX_DQA_CMD_QUEUE);
	if (err) {
		IWX_ERR(sc, "could not allocate TX ring %d\n",
		    IWX_DQA_CMD_QUEUE);
		goto fail;
	}

	err = iwx_alloc_rx_ring(sc, &sc->rxq);
	if (err) {
		IWX_ERR(sc, "could not allocate RX ring\n");
		goto fail;
	}

	/* D-CMD3 */
	err = iwx_dma_contig_alloc(sc, &sc->cmd_big_dma, IWX_PAGE_SIZE,
	    IWX_FIRST_TB_SIZE_ALIGN);
	if (err) {
		IWX_ERR(sc, "could not allocate large command buffer\n");
		goto fail;
	}

	for (i = 0; i < (int)nitems(sc->sc_phyctxt); i++) {
		sc->sc_phyctxt[i].id = (uint16_t)i;
		sc->sc_phyctxt[i].sco = 0;		/* SCO_SCN */
		sc->sc_phyctxt[i].vht_chan_width = 0;	/* CHAN_WIDTH_HT */
	}

	IWX_LOG(sc, "hw rev 0x%x rf id 0x%x crf 0x%x cnv 0x%x, firmware %s\n",
	    sc->sc_hw_rev, sc->sc_hw_rf_id, sc->sc_hw_crf_id,
	    sc->sc_hw_cnv_id, sc->sc_fwname);
	return 0;

 fail:
	iwx_detach(sc);
	return err ? err : ENOMEM;
}

void
iwx_detach(struct iwx_softc *sc)
{
	int i;

	iwx_ctxt_info_free_fw_img(sc);
	iwx_ctxt_info_free_paging(sc);
	iwx_dma_contig_free(sc, &sc->iml_dma);
	iwx_dma_contig_free(sc, &sc->cmd_big_dma);
	iwx_free_rx_ring(sc, &sc->rxq);
	for (i = 0; i < (int)nitems(sc->txq); i++)
		iwx_free_tx_ring(sc, &sc->txq[i]);
	iwx_dma_contig_free(sc, &sc->ict_dma);
	iwx_dma_contig_free(sc, &sc->prph_info_dma);
	iwx_dma_contig_free(sc, &sc->prph_scratch_dma);
	iwx_dma_contig_free(sc, &sc->ctxt_info_dma);
	for (i = 0; i < IWX_TX_RING_COUNT; i++) {
		if (sc->sc_cmd_resp_pkt[i] != NULL) {
			osdep_free(sc->sc_cmd_resp_pkt[i],
			    sc->sc_cmd_resp_len[i]);
			sc->sc_cmd_resp_pkt[i] = NULL;
		}
	}
	iwx_fw_info_free(sc);
	sc->sc_fw.fw_status = IWX_FW_STATUS_NONE;
}

void
iwx_post_alive(struct iwx_softc *sc)
{
	int txcmd_ver;

	iwx_ict_reset(sc);

	txcmd_ver = iwx_lookup_notif_ver(sc, IWX_LONG_GROUP, IWX_TX_CMD) ;
	if (txcmd_ver != IWX_FW_CMD_VER_UNKNOWN && txcmd_ver > 6)
		sc->sc_rate_n_flags_version = 2;
	else
		sc->sc_rate_n_flags_version = 1;
}

int
iwx_load_pnvm(struct iwx_softc *sc)
{
	const int wait_flags = IWX_PNVM_COMPLETE;
	int err = 0;

	if (sc->sc_sku_id[0] == 0 &&
	    sc->sc_sku_id[1] == 0 &&
	    sc->sc_sku_id[2] == 0)
		return 0;

	if (sc->sc_pnvm_name) {
		/* D-IN5: iwx_pnvm_parse() not ported */
		IWX_ERR(sc, "PNVM %s required but not supported\n",
		    sc->sc_pnvm_name);
		return ENOTSUP;
	}

	if (!iwx_nic_lock(sc))
		return EBUSY;

	/*
	 * If we don't have a platform NVM file simply ask firmware
	 * to proceed without it.
	 */

	iwx_write_umac_prph(sc, IWX_UREG_DOORBELL_TO_ISR6,
	    IWX_UREG_DOORBELL_TO_ISR6_PNVM);

	/* Wait for the pnvm complete notification from firmware. */
	err = iwx_wait_cond(sc, OSDEP_EV_INIT, &sc->sc_init_complete,
	    wait_flags, 2000);

	iwx_nic_unlock(sc);
	return err;
}

int
iwx_load_ucode_wait_alive(struct iwx_softc *sc)
{
	int err;

	err = iwx_read_firmware(sc);
	if (err)
		return err;

	err = iwx_start_fw(sc);
	if (err)
		return err;

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		err = iwx_load_pnvm(sc);
		if (err)
			return err;
	}

	iwx_post_alive(sc);

	return 0;
}

int
iwx_run_init_mvm_ucode(struct iwx_softc *sc, int readnvm)
{
	const int wait_flags = IWX_INIT_COMPLETE;
	struct iwx_nvm_access_complete_cmd nvm_complete;
	struct iwx_init_extended_cfg_cmd init_cfg;
	int err;

	memset(&nvm_complete, 0, sizeof(nvm_complete));
	memset(&init_cfg, 0, sizeof(init_cfg));
	init_cfg.init_flags = htole32(IWX_INIT_NVM);

	if ((sc->sc_flags & IWX_FLAG_RFKILL) && !readnvm) {
		IWX_ERR(sc, "radio is disabled by hardware switch\n");
		return EPERM;
	}

	sc->sc_init_complete = 0;
	err = iwx_load_ucode_wait_alive(sc);
	if (err) {
		IWX_ERR(sc, "failed to load init firmware\n");
		return err;
	}

	/*
	 * Send init config command to mark that we are sending NVM
	 * access commands
	 */
	err = iwx_send_cmd_pdu(sc, IWX_WIDE_ID(IWX_SYSTEM_GROUP,
	    IWX_INIT_EXTENDED_CFG_CMD), 0, sizeof(init_cfg), &init_cfg);
	if (err)
		return err;

	err = iwx_send_cmd_pdu(sc, IWX_WIDE_ID(IWX_REGULATORY_AND_NVM_GROUP,
	    IWX_NVM_ACCESS_COMPLETE), 0, sizeof(nvm_complete), &nvm_complete);
	if (err)
		return err;

	/* Wait for the init complete notification from the firmware. */
	err = iwx_wait_cond(sc, OSDEP_EV_INIT, &sc->sc_init_complete,
	    wait_flags, 2000);
	if (err)
		return err;

	if (readnvm) {
		err = iwx_nvm_get(sc);
		if (err) {
			IWX_ERR(sc, "failed to read nvm\n");
			return err;
		}
		/* iwx: copy to ic_myaddr if unset */
		memcpy(sc->sc_macaddr, sc->sc_nvm.hw_addr, ETHER_ADDR_LEN);
	}

	/*
	 * Only enable the MLD API on MA/BZ devices for now as the API 77
	 * firmware on some of the older firmware devices also claims
	 * support, but doesn't actually work.
	 */
	if (isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_MLD_API_SUPPORT) &&
	    (IWX_CSR_HW_REV_TYPE(sc->sc_hw_rev) == IWX_CFG_MAC_TYPE_MA ||
	    sc->sc_device_family == IWX_DEVICE_FAMILY_BZ))
		sc->sc_use_mld_api = 1;

	return 0;
}

/*
 * NVM read access and content parsing.  We do not support
 * external NVM or writing NVM.
 */

uint8_t
iwx_fw_valid_tx_ant(struct iwx_softc *sc)
{
	uint8_t tx_ant;

	tx_ant = (uint8_t)((sc->sc_fw_phy_config & IWX_FW_PHY_CFG_TX_CHAIN)
	    >> IWX_FW_PHY_CFG_TX_CHAIN_POS);

	if (sc->sc_nvm.valid_tx_ant)
		tx_ant &= sc->sc_nvm.valid_tx_ant;

	return tx_ant;
}

uint8_t
iwx_fw_valid_rx_ant(struct iwx_softc *sc)
{
	uint8_t rx_ant;

	rx_ant = (uint8_t)((sc->sc_fw_phy_config & IWX_FW_PHY_CFG_RX_CHAIN)
	    >> IWX_FW_PHY_CFG_RX_CHAIN_POS);

	if (sc->sc_nvm.valid_rx_ant)
		rx_ant &= sc->sc_nvm.valid_rx_ant;

	return rx_ant;
}

/* D-IN7 */
void
iwx_init_channel_map(struct iwx_softc *sc, const uint16_t *channel_profile_v3,
    const uint32_t *channel_profile_v4, int nchan_profile)
{
	struct iwx_nvm_data *data = &sc->sc_nvm;
	int ch_idx;
	uint32_t ch_flags;
	int is_5ghz;
	int nchan;
	const uint8_t *nvm_channels;

	/* sc_uhb_supported is 0 for So+Hr: 6 GHz list never used */
	nchan = (int)nitems(iwx_nvm_channels_8000);
	nvm_channels = iwx_nvm_channels_8000;

	data->nchan = 0;
	memset(data->channels, 0, sizeof(data->channels));

	for (ch_idx = 0; ch_idx < nchan && ch_idx < nchan_profile; ch_idx++) {
		if (channel_profile_v4)
			ch_flags = le32toh(channel_profile_v4[ch_idx]);
		else
			ch_flags = le16toh(channel_profile_v3[ch_idx]);

		/* net80211 cannot handle 6 GHz channel numbers yet */
		if (ch_idx >= IWX_NUM_2GHZ_CHANNELS + IWX_NUM_5GHZ_CHANNELS)
			break;

		is_5ghz = ch_idx >= IWX_NUM_2GHZ_CHANNELS;
		if (is_5ghz && !data->sku_cap_band_52GHz_enable)
			ch_flags &= ~IWX_NVM_CHANNEL_VALID;

		if (!(ch_flags & IWX_NVM_CHANNEL_VALID))
			continue;

		data->channels[data->nchan].ieee = nvm_channels[ch_idx];
		data->channels[data->nchan].nvm_flags = ch_flags;
		data->nchan++;
	}
}

static void
iwx_flip_hw_address(uint32_t mac_addr0, uint32_t mac_addr1, uint8_t *dest)
{
	const uint8_t *hw_addr;

	hw_addr = (const uint8_t *)&mac_addr0;
	dest[0] = hw_addr[3];
	dest[1] = hw_addr[2];
	dest[2] = hw_addr[1];
	dest[3] = hw_addr[0];

	hw_addr = (const uint8_t *)&mac_addr1;
	dest[4] = hw_addr[1];
	dest[5] = hw_addr[0];
}

int
iwx_is_valid_mac_addr(const uint8_t *addr)
{
	static const uint8_t reserved_mac[] = {
		0x02, 0xcc, 0xaa, 0xff, 0xee, 0x00
	};
	static const uint8_t etherbroadcastaddr[ETHER_ADDR_LEN] = {
		0xff, 0xff, 0xff, 0xff, 0xff, 0xff
	};
	static const uint8_t etheranyaddr[ETHER_ADDR_LEN] = {
		0, 0, 0, 0, 0, 0
	};

	return (memcmp(reserved_mac, addr, ETHER_ADDR_LEN) != 0 &&
	    memcmp(etherbroadcastaddr, addr, sizeof(etherbroadcastaddr)) != 0 &&
	    memcmp(etheranyaddr, addr, sizeof(etheranyaddr)) != 0 &&
	    !(addr[0] & 0x01) /* ETHER_IS_MULTICAST */);
}

static void
iwx_set_mac_addr_from_csr(struct iwx_softc *sc, struct iwx_nvm_data *data)
{
	uint32_t mac_addr0, mac_addr1;

	memset(data->hw_addr, 0, sizeof(data->hw_addr));

	if (!iwx_nic_lock(sc))
		return;

	mac_addr0 = htole32(IWX_READ(sc, IWX_CSR_MAC_ADDR0_STRAP(sc)));
	mac_addr1 = htole32(IWX_READ(sc, IWX_CSR_MAC_ADDR1_STRAP(sc)));

	iwx_flip_hw_address(mac_addr0, mac_addr1, data->hw_addr);

	/* If OEM fused a valid address, use it instead of the one in OTP. */
	if (iwx_is_valid_mac_addr(data->hw_addr)) {
		iwx_nic_unlock(sc);
		return;
	}

	mac_addr0 = htole32(IWX_READ(sc, IWX_CSR_MAC_ADDR0_OTP(sc)));
	mac_addr1 = htole32(IWX_READ(sc, IWX_CSR_MAC_ADDR1_OTP(sc)));

	iwx_flip_hw_address(mac_addr0, mac_addr1, data->hw_addr);

	iwx_nic_unlock(sc);
}

int
iwx_nvm_get(struct iwx_softc *sc)
{
	struct iwx_nvm_get_info cmd;
	struct iwx_nvm_data *nvm = &sc->sc_nvm;
	struct iwx_host_cmd hcmd;
	int err;
	uint32_t mac_flags;
	/*
	 * All the values in iwx_nvm_get_info_rsp v4 are the same as
	 * in v3, except for the channel profile part of the
	 * regulatory.  So we can just access the new struct, with the
	 * exception of the latter.
	 */
	const struct iwx_nvm_get_info_rsp *rsp;
	const struct iwx_nvm_get_info_rsp_v3 *rsp_v3;
	int v4 = isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_REGULATORY_NVM_INFO);
	size_t resp_len = v4 ? sizeof(*rsp) : sizeof(*rsp_v3);

	memset(&cmd, 0, sizeof(cmd));
	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.flags = IWX_CMD_WANT_RESP | IWX_CMD_SEND_IN_RFKILL;
	hcmd.data[0] = &cmd;
	hcmd.len[0] = sizeof(cmd);
	hcmd.id = IWX_WIDE_ID(IWX_REGULATORY_AND_NVM_GROUP, IWX_NVM_GET_INFO);
	hcmd.resp_pkt_len = sizeof(struct iwx_rx_packet) + resp_len;

	err = iwx_send_cmd(sc, &hcmd);
	if (err)
		return err;

	if (hcmd.resp_pkt == NULL) {		/* D-IN6 */
		err = EIO;
		goto out;
	}
	if ((size_t)iwx_rx_packet_payload_len(hcmd.resp_pkt) != resp_len) {
		err = EIO;
		goto out;
	}

	memset(nvm, 0, sizeof(*nvm));

	iwx_set_mac_addr_from_csr(sc, nvm);
	if (!iwx_is_valid_mac_addr(nvm->hw_addr)) {
		IWX_ERR(sc, "no valid mac address was found\n");
		err = EINVAL;
		goto out;
	}

	rsp = (const void *)hcmd.resp_pkt->data;

	/* Initialize general data */
	nvm->nvm_version = le16toh(rsp->general.nvm_version);
	nvm->n_hw_addrs = rsp->general.n_hw_addrs;

	/* Initialize MAC sku data */
	mac_flags = le32toh(rsp->mac_sku.mac_sku_flags);
	nvm->sku_cap_11ac_enable =
		!!(mac_flags & IWX_NVM_MAC_SKU_FLAGS_802_11AC_ENABLED);
	nvm->sku_cap_11n_enable =
		!!(mac_flags & IWX_NVM_MAC_SKU_FLAGS_802_11N_ENABLED);
	nvm->sku_cap_11ax_enable =
		!!(mac_flags & IWX_NVM_MAC_SKU_FLAGS_802_11AX_ENABLED);
	nvm->sku_cap_band_24GHz_enable =
		!!(mac_flags & IWX_NVM_MAC_SKU_FLAGS_BAND_2_4_ENABLED);
	nvm->sku_cap_band_52GHz_enable =
		!!(mac_flags & IWX_NVM_MAC_SKU_FLAGS_BAND_5_2_ENABLED);
	nvm->sku_cap_mimo_disable =
		!!(mac_flags & IWX_NVM_MAC_SKU_FLAGS_MIMO_DISABLED);

	/* Initialize PHY sku data */
	nvm->valid_tx_ant = (uint8_t)le32toh(rsp->phy_sku.tx_chains);
	nvm->valid_rx_ant = (uint8_t)le32toh(rsp->phy_sku.rx_chains);

	if (le32toh(rsp->regulatory.lar_enabled) &&
	    isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_LAR_SUPPORT)) {
		nvm->lar_enabled = 1;
	}

	if (v4) {
		iwx_init_channel_map(sc, NULL,
		    rsp->regulatory.channel_profile, IWX_NUM_CHANNELS);
	} else {
		rsp_v3 = (const void *)rsp;
		iwx_init_channel_map(sc, rsp_v3->regulatory.channel_profile,
		    NULL, IWX_NUM_CHANNELS_V1);
	}
out:
	iwx_free_resp(sc, &hcmd);
	return err;
}

/* ---- iwx_init_hw() and the commands it sends ---------------------------- */

static int
iwx_send_tx_ant_cfg(struct iwx_softc *sc, uint8_t valid_tx_ant)
{
	struct iwx_tx_ant_cfg_cmd tx_ant_cmd;

	memset(&tx_ant_cmd, 0, sizeof(tx_ant_cmd));
	tx_ant_cmd.valid = htole32(valid_tx_ant);

	return iwx_send_cmd_pdu(sc, IWX_TX_ANT_CONFIGURATION_CMD,
	    0, sizeof(tx_ant_cmd), &tx_ant_cmd);
}

static int
iwx_send_phy_cfg_cmd(struct iwx_softc *sc)
{
	struct iwx_phy_cfg_cmd phy_cfg_cmd;

	phy_cfg_cmd.phy_cfg = htole32(sc->sc_fw_phy_config);
	phy_cfg_cmd.calib_control.event_trigger =
	    sc->sc_default_calib[IWX_UCODE_TYPE_REGULAR].event_trigger;
	phy_cfg_cmd.calib_control.flow_trigger =
	    sc->sc_default_calib[IWX_UCODE_TYPE_REGULAR].flow_trigger;

	return iwx_send_cmd_pdu(sc, IWX_PHY_CONFIGURATION_CMD, 0,
	    sizeof(phy_cfg_cmd), &phy_cfg_cmd);
}

static int
iwx_send_dqa_cmd(struct iwx_softc *sc)
{
	struct iwx_dqa_enable_cmd dqa_cmd;
	uint32_t cmd_id;

	memset(&dqa_cmd, 0, sizeof(dqa_cmd));
	dqa_cmd.cmd_queue = htole32(IWX_DQA_CMD_QUEUE);

	cmd_id = iwx_cmd_id(IWX_DQA_ENABLE_CMD, IWX_DATA_PATH_GROUP, 0);
	return iwx_send_cmd_pdu(sc, cmd_id, 0, sizeof(dqa_cmd), &dqa_cmd);
}

static int
iwx_send_bt_init_conf(struct iwx_softc *sc)
{
	struct iwx_bt_coex_cmd bt_cmd;

	bt_cmd.mode = htole32(IWX_BT_COEX_WIFI);
	bt_cmd.enabled_modules = 0;

	return iwx_send_cmd_pdu(sc, IWX_BT_CONFIG, 0, sizeof(bt_cmd),
	    &bt_cmd);
}

static int
iwx_send_soc_conf(struct iwx_softc *sc)
{
	struct iwx_soc_configuration_cmd cmd;
	int err;
	uint32_t cmd_id, flags = 0;

	memset(&cmd, 0, sizeof(cmd));

	/*
	 * In VER_1 of this command, the discrete value is considered
	 * an integer; In VER_2, it's a bitmask.  Since we have only 2
	 * values in VER_1, this is backwards-compatible with VER_2,
	 * as long as we don't set any other flag bits.
	 */
	if (!sc->sc_integrated) { /* VER_1 */
		flags = IWX_SOC_CONFIG_CMD_FLAGS_DISCRETE;
	} else { /* VER_2 */
		uint8_t scan_cmd_ver;
		if (sc->sc_ltr_delay != IWX_SOC_FLAGS_LTR_APPLY_DELAY_NONE)
			flags |= (sc->sc_ltr_delay &
			    IWX_SOC_FLAGS_LTR_APPLY_DELAY_MASK);
		scan_cmd_ver = iwx_lookup_cmd_ver(sc, IWX_LONG_GROUP,
		    IWX_SCAN_REQ_UMAC);
		if (scan_cmd_ver != IWX_FW_CMD_VER_UNKNOWN &&
		    scan_cmd_ver >= 2 && sc->sc_low_latency_xtal)
			flags |= IWX_SOC_CONFIG_CMD_FLAGS_LOW_LATENCY;
	}
	cmd.flags = htole32(flags);

	cmd.latency = htole32(sc->sc_xtal_latency);

	cmd_id = iwx_cmd_id(IWX_SOC_CONFIGURATION_CMD, IWX_SYSTEM_GROUP, 0);
	err = iwx_send_cmd_pdu(sc, cmd_id, 0, sizeof(cmd), &cmd);
	if (err)
		IWX_ERR(sc, "failed to set soc latency: %d\n", err);
	return err;
}

static int
iwx_config_ltr(struct iwx_softc *sc)
{
	struct iwx_ltr_config_cmd cmd;

	memset(&cmd, 0, sizeof(cmd));
	cmd.flags = htole32(IWX_LTR_CFG_FLAG_FEATURE_ENABLE);

	if (!sc->sc_ltr_enabled)
		return 0;

	return iwx_send_cmd_pdu(sc, IWX_LTR_CONFIG, 0, sizeof(cmd), &cmd);
}

static int
iwx_send_temp_report_ths_cmd(struct iwx_softc *sc)
{
	struct iwx_temp_report_ths_cmd cmd;
	int err;

	/*
	 * In order to give responsibility for critical-temperature-kill
	 * and TX backoff to FW we need to send an empty temperature
	 * reporting command at init time.
	 */
	memset(&cmd, 0, sizeof(cmd));

	err = iwx_send_cmd_pdu(sc,
	    IWX_WIDE_ID(IWX_PHY_OPS_GROUP, IWX_TEMP_REPORTING_THRESHOLDS_CMD),
	    0, sizeof(cmd), &cmd);
	if (err)
		IWX_ERR(sc, "TEMP_REPORT_THS_CMD command failed (error %d)\n",
		    err);

	return err;
}

/* iwx_set_pslevel(sc, 0, level, 0) up to the device power command. */
static int
iwx_power_update_device(struct iwx_softc *sc, int level)
{
	struct iwx_device_power_cmd dcmd;

	memset(&dcmd, 0, sizeof(dcmd));
	if (level != 0)
		dcmd.flags = htole16(IWX_DEVICE_POWER_FLAGS_POWER_SAVE_ENA_MSK);

	/*
	 * The MAC power command (IWX_MAC_PM_POWER_TABLE) follows in iwx
	 * only when a MAC context is active; that is M2 territory.
	 */
	return iwx_send_cmd_pdu(sc, IWX_POWER_TABLE_CMD, 0,
	    sizeof(dcmd), &dcmd);
}

int
iwx_send_update_mcc_cmd(struct iwx_softc *sc, const char *alpha2)
{
	struct iwx_mcc_update_cmd mcc_cmd;
	struct iwx_host_cmd hcmd;
	const struct iwx_rx_packet *pkt;
	size_t resp_len;
	int err, resp_version;

	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.id = IWX_MCC_UPDATE_CMD;
	hcmd.flags = IWX_CMD_WANT_RESP;
	hcmd.data[0] = &mcc_cmd;

	resp_version = iwx_lookup_notif_ver(sc, IWX_LONG_GROUP,
	    IWX_MCC_UPDATE_CMD);
	if (resp_version != IWX_FW_CMD_VER_UNKNOWN && resp_version >= 8) {
		IWX_ERR(sc, "unsupported MCC update command response "
		    "version %u\n", resp_version);
		return ENOTSUP;
	}

	memset(&mcc_cmd, 0, sizeof(mcc_cmd));
	mcc_cmd.mcc = htole16((uint16_t)(alpha2[0] << 8 | alpha2[1]));
	if (isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_WIFI_MCC_UPDATE) ||
	    isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_LAR_MULTI_MCC))
		mcc_cmd.source_id = IWX_MCC_SOURCE_GET_CURRENT;
	else
		mcc_cmd.source_id = IWX_MCC_SOURCE_OLD_FW;

	hcmd.len[0] = sizeof(struct iwx_mcc_update_cmd);
	hcmd.resp_pkt_len = IWX_CMD_RESP_MAX;

	err = iwx_send_cmd(sc, &hcmd);
	if (err)
		return err;

	pkt = hcmd.resp_pkt;
	if (!pkt || (pkt->hdr.flags & IWX_CMD_FAILED_MSK)) {
		err = EIO;
		goto out;
	}

	resp_len = (size_t)iwx_rx_packet_payload_len(pkt);

	if (isset(sc->sc_enabled_capa,
	    IWX_UCODE_TLV_CAPA_MCC_UPDATE_11AX_SUPPORT)) {
		const struct iwx_mcc_update_resp_v4 *resp;

		if (resp_len < sizeof(*resp)) {
			err = EIO;
			goto out;
		}

		resp = (const void *)pkt->data;
		if (resp_len != sizeof(*resp) +
		    resp->n_channels * sizeof(resp->channels[0])) {
			err = EIO;
			goto out;
		}

		DPRINTF(sc, "MCC status=0x%x mcc=0x%x cap=0x%x time=0x%x "
		    "geo_info=0x%x source_id=0x%d n_channels=%u\n",
		    resp->status, resp->mcc, resp->cap, resp->time,
		    resp->geo_info, resp->source_id, resp->n_channels);

		/* Update channel map and our scan configuration. */
		iwx_init_channel_map(sc, NULL, resp->channels,
		    (int)resp->n_channels);
	} else {
		const struct iwx_mcc_update_resp *resp;

		if (resp_len < sizeof(*resp)) {
			err = EIO;
			goto out;
		}
		resp = (const void *)pkt->data;
		if (resp_len != sizeof(*resp) +
		    resp->n_channels * sizeof(resp->channels[0])) {
			err = EIO;
			goto out;
		}

		DPRINTF(sc, "MCC status=0x%x mcc=0x%x cap=0x%x time=0x%x "
		    "geo_info=0x%x source_id=0x%d n_channels=%u\n",
		    resp->status, resp->mcc, resp->cap, resp->time,
		    resp->geo_info, resp->source_id, resp->n_channels);

		/* Update channel map and our scan configuration. */
		iwx_init_channel_map(sc, NULL, resp->channels,
		    (int)resp->n_channels);
	}

out:
	iwx_free_resp(sc, &hcmd);

	return err;
}

static int
iwx_config_umac_scan_reduced(struct iwx_softc *sc)
{
	struct iwx_scan_config scan_cfg;
	struct iwx_host_cmd hcmd;
	int cmdver;

	memset(&hcmd, 0, sizeof(hcmd));
	hcmd.id = iwx_cmd_id(IWX_SCAN_CFG_CMD, IWX_LONG_GROUP, 0);
	hcmd.len[0] = sizeof(scan_cfg);
	hcmd.data[0] = &scan_cfg;
	hcmd.flags = 0;

	if (!isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_REDUCED_SCAN_CONFIG)) {
		IWX_ERR(sc, "firmware does not support reduced scan config\n");
		return ENOTSUP;
	}

	memset(&scan_cfg, 0, sizeof(scan_cfg));

	/*
	 * SCAN_CFG version >= 5 implies that the broadcast
	 * STA ID field is deprecated.
	 */
	cmdver = iwx_lookup_cmd_ver(sc, IWX_LONG_GROUP, IWX_SCAN_CFG_CMD);
	if (cmdver == IWX_FW_CMD_VER_UNKNOWN || cmdver < 5)
		scan_cfg.bcast_sta_id = 0xff;

	scan_cfg.tx_chains = htole32(iwx_fw_valid_tx_ant(sc));
	scan_cfg.rx_chains = htole32(iwx_fw_valid_rx_ant(sc));

	return iwx_send_cmd(sc, &hcmd);
}

static int
iwx_disable_beacon_filter(struct iwx_softc *sc)
{
	struct iwx_beacon_filter_cmd cmd;
	int err;

	memset(&cmd, 0, sizeof(cmd));

	err = iwx_send_cmd_pdu(sc, IWX_REPLY_BEACON_FILTERING_CMD,
	    0, sizeof(struct iwx_beacon_filter_cmd), &cmd);
	if (err == 0)
		sc->sc_bf_enabled = 0;

	return err;
}

int
iwx_init_hw(struct iwx_softc *sc)
{
	int err, i;

	err = iwx_run_init_mvm_ucode(sc, 0);
	if (err)
		return err;

	if (!iwx_nic_lock(sc))
		return EBUSY;

	err = iwx_send_tx_ant_cfg(sc, iwx_fw_valid_tx_ant(sc));
	if (err) {
		IWX_ERR(sc, "could not init tx ant config (error %d)\n", err);
		goto err;
	}

	if (sc->sc_tx_with_siso_diversity) {
		err = iwx_send_phy_cfg_cmd(sc);
		if (err) {
			IWX_ERR(sc, "could not send phy config (error %d)\n",
			    err);
			goto err;
		}
	}

	err = iwx_send_bt_init_conf(sc);
	if (err) {
		IWX_ERR(sc, "could not init bt coex (error %d)\n", err);
		goto err;	/* D-IN3 */
	}

	err = iwx_send_soc_conf(sc);
	if (err)
		goto err;	/* D-IN3 */

	if (isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_DQA_SUPPORT)) {
		err = iwx_send_dqa_cmd(sc);
		if (err)
			goto err;	/* D-IN3 */
	}

	for (i = 0; i < IWX_NUM_PHY_CTX; i++) {
		/*
		 * The channel used here isn't relevant as it's
		 * going to be overwritten in the other flows.
		 * For now use the first channel we have.
		 */
		sc->sc_phyctxt[i].id = (uint16_t)i;
		sc->sc_phyctxt[i].channel = 1;
	}

	err = iwx_config_ltr(sc);
	if (err) {
		IWX_ERR(sc, "PCIe LTR configuration failed (error %d)\n", err);
	}

	if (isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_CT_KILL_BY_FW)) {
		err = iwx_send_temp_report_ths_cmd(sc);
		if (err)
			goto err;
	}

	err = iwx_power_update_device(sc, 0);	/* D-IN4 */
	if (err) {
		IWX_ERR(sc, "could not send power command (error %d)\n", err);
		goto err;
	}

	if (sc->sc_nvm.lar_enabled) {
		err = iwx_send_update_mcc_cmd(sc, "ZZ");
		if (err) {
			IWX_ERR(sc, "could not init LAR (error %d)\n", err);
			goto err;
		}
	}

	err = iwx_config_umac_scan_reduced(sc);
	if (err) {
		IWX_ERR(sc, "could not configure scan (error %d)\n", err);
		goto err;
	}

	err = iwx_disable_beacon_filter(sc);
	if (err) {
		IWX_ERR(sc, "could not disable beacon filter (error %d)\n",
		    err);
		goto err;
	}

err:
	iwx_nic_unlock(sc);
	return err;
}

/* ---- public lifecycle (iwx_preinit / iwx_init / iwx_stop) --------------- */

int
iwx_preinit(struct iwx_softc *sc)
{
	int err;

	err = iwx_prepare_card_hw(sc);
	if (err) {
		IWX_ERR(sc, "could not initialize hardware\n");
		return err;
	}

	if (sc->attached)
		return 0;

	err = iwx_start_hw(sc);
	if (err) {
		IWX_ERR(sc, "could not initialize hardware\n");
		return err;
	}

	err = iwx_run_init_mvm_ucode(sc, 1);
	iwx_stop_device(sc);
	if (err)
		return err;

	/* Print version info and MAC address on first successful fw load. */
	sc->attached = 1;
	IWX_LOG(sc, "hw rev 0x%x, fw %s, address "
	    "%02x:%02x:%02x:%02x:%02x:%02x, %d channels, lar %d\n",
	    sc->sc_hw_rev & IWX_CSR_HW_REV_TYPE_MSK, sc->sc_fwver,
	    sc->sc_nvm.hw_addr[0], sc->sc_nvm.hw_addr[1],
	    sc->sc_nvm.hw_addr[2], sc->sc_nvm.hw_addr[3],
	    sc->sc_nvm.hw_addr[4], sc->sc_nvm.hw_addr[5],
	    sc->sc_nvm.nchan, sc->sc_nvm.lar_enabled);

	return 0;
}

int
iwx_init(struct iwx_softc *sc)
{
	int err, generation;

	generation = ++sc->sc_generation;
	sc->sc_flags &= ~(IWX_FLAG_FW_ERR | IWX_FLAG_HW_ERR);

	err = iwx_preinit(sc);
	if (err)
		return err;

	err = iwx_start_hw(sc);
	if (err) {
		IWX_ERR(sc, "could not initialize hardware\n");
		return err;
	}

	err = iwx_init_hw(sc);
	if (err) {
		if (generation == sc->sc_generation)
			iwx_stop_device(sc);
		sc->sc_state = IWX_STATE_ERROR;
		return err;
	}

	sc->sc_state = IWX_STATE_FW_READY;
	return 0;
}

void
iwx_stop(struct iwx_softc *sc)
{
	sc->sc_flags |= IWX_FLAG_SHUTDOWN; /* Disallow new fatal reports. */
	sc->sc_rx_enabled = 0;

	iwx_stop_device(sc);

	/* Reset soft state. */
	sc->sc_generation++;

	sc->sc_flags &= ~IWX_FLAG_MAC_ACTIVE;
	sc->sc_flags &= ~IWX_FLAG_BINDING_ACTIVE;
	sc->sc_flags &= ~IWX_FLAG_STA_ACTIVE;
	sc->sc_flags &= ~IWX_FLAG_TE_ACTIVE;
	sc->sc_flags &= ~IWX_FLAG_HW_ERR;
	sc->sc_flags &= ~IWX_FLAG_FW_ERR;
	sc->sc_flags &= ~IWX_FLAG_SHUTDOWN;
	sc->sc_flags &= ~IWX_FLAG_PHY_ACTIVE;

	sc->sc_state = IWX_STATE_DOWN;
}

void
iwx_get_info(struct iwx_softc *sc, struct iwx_info *info)
{
	int i;

	memset(info, 0, sizeof(*info));
	memcpy(info->macaddr, sc->sc_macaddr, ETHER_ADDR_LEN);
	memcpy(info->fwver, sc->sc_fwver, sizeof(info->fwver));
	info->fwver[sizeof(info->fwver) - 1] = '\0';
	info->hw_rev = (uint32_t)sc->sc_hw_rev;
	info->hw_rf_id = (uint32_t)sc->sc_hw_rf_id;
	info->state = sc->sc_state;
	info->rfkill = (sc->sc_flags & IWX_FLAG_RFKILL) ? 1 : 0;
	info->msix = sc->sc_msix;
	info->last_fw_error = sc->sc_last_fw_error;
	info->rx_frames = sc->sc_stat_rx_frames;
	info->rx_dropped = sc->sc_stat_rx_dropped;
	for (i = 0; i < sc->sc_nvm.nchan && i < (int)nitems(info->chan); i++) {
		info->chan[i] = sc->sc_nvm.channels[i].ieee;
		info->chan_flags[i] = sc->sc_nvm.channels[i].nvm_flags;
	}
	info->nchan = i;
}

/* ---- firmware error log ------------------------------------------------- */

/*
 * Note: This structure is read from the device with IO accesses,
 * and the reading already does the endian conversion. As it is
 * read with uint32_t-sized accesses, any members with a different size
 * need to be ordered correctly though!
 */
struct iwx_error_event_table {
	uint32_t valid;		/* (nonzero) valid, (0) log is empty */
	uint32_t error_id;		/* type of error */
	uint32_t trm_hw_status0;	/* TRM HW status */
	uint32_t trm_hw_status1;	/* TRM HW status */
	uint32_t blink2;		/* branch link */
	uint32_t ilink1;		/* interrupt link */
	uint32_t ilink2;		/* interrupt link */
	uint32_t data1;		/* error-specific data */
	uint32_t data2;		/* error-specific data */
	uint32_t data3;		/* error-specific data */
	uint32_t bcon_time;		/* beacon timer */
	uint32_t tsf_low;		/* network timestamp function timer */
	uint32_t tsf_hi;		/* network timestamp function timer */
	uint32_t gp1;		/* GP1 timer register */
	uint32_t gp2;		/* GP2 timer register */
	uint32_t fw_rev_type;	/* firmware revision type */
	uint32_t major;		/* uCode version major */
	uint32_t minor;		/* uCode version minor */
	uint32_t hw_ver;		/* HW Silicon version */
	uint32_t brd_ver;		/* HW board version */
	uint32_t log_pc;		/* log program counter */
	uint32_t frame_ptr;		/* frame pointer */
	uint32_t stack_ptr;		/* stack pointer */
	uint32_t hcmd;		/* last host command header */
	uint32_t isr0;		/* isr status register LMPM_NIC_ISR0:
				 * rxtx_flag */
	uint32_t isr1;		/* isr status register LMPM_NIC_ISR1:
				 * host_flag */
	uint32_t isr2;		/* isr status register LMPM_NIC_ISR2:
				 * enc_flag */
	uint32_t isr3;		/* isr status register LMPM_NIC_ISR3:
				 * time_flag */
	uint32_t isr4;		/* isr status register LMPM_NIC_ISR4:
				 * wico interrupt */
	uint32_t last_cmd_id;	/* last HCMD id handled by the firmware */
	uint32_t wait_event;		/* wait event() caller address */
	uint32_t l2p_control;	/* L2pControlField */
	uint32_t l2p_duration;	/* L2pDurationField */
	uint32_t l2p_mhvalid;	/* L2pMhValidBits */
	uint32_t l2p_addr_match;	/* L2pAddrMatchStat */
	uint32_t lmpm_pmg_sel;	/* indicate which clocks are turned on
				 * (LMPM_PMG_SEL) */
	uint32_t u_timestamp;	/* indicate when the date and time of the
				 * compilation */
	uint32_t flow_handler;	/* FH read/write pointers, RX credit */
} __packed /* LOG_ERROR_TABLE_API_S_VER_3 */;

/*
 * UMAC error struct - relevant starting from family 8000 chip.
 */
struct iwx_umac_error_event_table {
	uint32_t valid;		/* (nonzero) valid, (0) log is empty */
	uint32_t error_id;	/* type of error */
	uint32_t blink1;	/* branch link */
	uint32_t blink2;	/* branch link */
	uint32_t ilink1;	/* interrupt link */
	uint32_t ilink2;	/* interrupt link */
	uint32_t data1;		/* error-specific data */
	uint32_t data2;		/* error-specific data */
	uint32_t data3;		/* error-specific data */
	uint32_t umac_major;
	uint32_t umac_minor;
	uint32_t frame_pointer;	/* core register 27*/
	uint32_t stack_pointer;	/* core register 28 */
	uint32_t cmd_header;	/* latest host cmd sent to UMAC */
	uint32_t nic_isr_pref;	/* ISR status register */
} __packed;

#define ERROR_START_OFFSET  (1 * sizeof(uint32_t))
#define ERROR_ELEM_SIZE     (7 * sizeof(uint32_t))

void
iwx_nic_umac_error(struct iwx_softc *sc)
{
	struct iwx_umac_error_event_table table;
	uint32_t base;
	uint32_t min_base = 0x400000;

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ)
		min_base = 0xD0000;

	base = sc->sc_uc.uc_umac_error_event_table;
	if (base < min_base) {
		IWX_ERR(sc, "Invalid error log pointer 0x%08x\n", base);
		return;
	}

	if (iwx_read_mem(sc, base, &table, sizeof(table)/sizeof(uint32_t))) {
		IWX_ERR(sc, "reading errlog failed\n");
		return;
	}

	if (ERROR_START_OFFSET <= table.valid * ERROR_ELEM_SIZE) {
		IWX_ERR(sc, "Start UMAC Error Log Dump:\n");
		IWX_ERR(sc, "Status: 0x%x, count: %d\n", sc->sc_flags,
		    table.valid);
	}

	IWX_ERR(sc, "0x%08X | %s\n", table.error_id,
		iwx_desc_lookup(table.error_id));
	IWX_ERR(sc, "0x%08X | umac branchlink1\n", table.blink1);
	IWX_ERR(sc, "0x%08X | umac branchlink2\n", table.blink2);
	IWX_ERR(sc, "0x%08X | umac interruptlink1\n", table.ilink1);
	IWX_ERR(sc, "0x%08X | umac interruptlink2\n", table.ilink2);
	IWX_ERR(sc, "0x%08X | umac data1\n", table.data1);
	IWX_ERR(sc, "0x%08X | umac data2\n", table.data2);
	IWX_ERR(sc, "0x%08X | umac data3\n", table.data3);
	IWX_ERR(sc, "0x%08X | umac major\n", table.umac_major);
	IWX_ERR(sc, "0x%08X | umac minor\n", table.umac_minor);
	IWX_ERR(sc, "0x%08X | frame pointer\n", table.frame_pointer);
	IWX_ERR(sc, "0x%08X | stack pointer\n", table.stack_pointer);
	IWX_ERR(sc, "0x%08X | last host cmd\n", table.cmd_header);
	IWX_ERR(sc, "0x%08X | isr status reg\n", table.nic_isr_pref);
}

#define IWX_FW_SYSASSERT_CPU_MASK 0xf0000000
static const struct {
	const char *name;
	uint8_t num;
} advanced_lookup[] = {
	{ "NMI_INTERRUPT_WDG", 0x34 },
	{ "SYSASSERT", 0x35 },
	{ "UCODE_VERSION_MISMATCH", 0x37 },
	{ "BAD_COMMAND", 0x38 },
	{ "BAD_COMMAND", 0x39 },
	{ "NMI_INTERRUPT_DATA_ACTION_PT", 0x3C },
	{ "FATAL_ERROR", 0x3D },
	{ "NMI_TRM_HW_ERR", 0x46 },
	{ "NMI_INTERRUPT_TRM", 0x4C },
	{ "NMI_INTERRUPT_BREAK_POINT", 0x54 },
	{ "NMI_INTERRUPT_WDG_RXF_FULL", 0x5C },
	{ "NMI_INTERRUPT_WDG_NO_RBD_RXF_FULL", 0x64 },
	{ "NMI_INTERRUPT_HOST", 0x66 },
	{ "NMI_INTERRUPT_LMAC_FATAL", 0x70 },
	{ "NMI_INTERRUPT_UMAC_FATAL", 0x71 },
	{ "NMI_INTERRUPT_OTHER_LMAC_FATAL", 0x73 },
	{ "NMI_INTERRUPT_ACTION_PT", 0x7C },
	{ "NMI_INTERRUPT_UNKNOWN", 0x84 },
	{ "NMI_INTERRUPT_INST_ACTION_PT", 0x86 },
	{ "ADVANCED_SYSASSERT", 0 },
};

const char *
iwx_desc_lookup(uint32_t num)
{
	int i;

	for (i = 0; i < (int)nitems(advanced_lookup) - 1; i++)
		if (advanced_lookup[i].num ==
		    (num & ~IWX_FW_SYSASSERT_CPU_MASK))
			return advanced_lookup[i].name;

	/* No entry matches 'num', so it is the last: ADVANCED_SYSASSERT */
	return advanced_lookup[i].name;
}

void
iwx_nic_error(struct iwx_softc *sc)
{
	struct iwx_error_event_table table;
	uint32_t base;
	uint32_t min_base = 0x400000;

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ)
		min_base = 0xD0000;

	IWX_ERR(sc, "dumping device error log\n");
	base = sc->sc_uc.uc_lmac_error_event_table[0];
	if (base < min_base) {
		IWX_ERR(sc, "Invalid error log pointer 0x%08x\n", base);
		return;
	}

	if (iwx_read_mem(sc, base, &table, sizeof(table)/sizeof(uint32_t))) {
		IWX_ERR(sc, "reading errlog failed\n");
		return;
	}

	if (!table.valid) {
		IWX_ERR(sc, "errlog not found, skipping\n");
		return;
	}

	sc->sc_last_fw_error = table.error_id;	/* D-IN8 */

	if (ERROR_START_OFFSET <= table.valid * ERROR_ELEM_SIZE) {
		IWX_ERR(sc, "Start Error Log Dump:\n");
		IWX_ERR(sc, "Status: 0x%x, count: %d\n", sc->sc_flags,
		    table.valid);
	}

	IWX_ERR(sc, "0x%08X | %-28s\n", table.error_id,
	    iwx_desc_lookup(table.error_id));
	IWX_ERR(sc, "%08X | trm_hw_status0\n", table.trm_hw_status0);
	IWX_ERR(sc, "%08X | trm_hw_status1\n", table.trm_hw_status1);
	IWX_ERR(sc, "%08X | branchlink2\n", table.blink2);
	IWX_ERR(sc, "%08X | interruptlink1\n", table.ilink1);
	IWX_ERR(sc, "%08X | interruptlink2\n", table.ilink2);
	IWX_ERR(sc, "%08X | data1\n", table.data1);
	IWX_ERR(sc, "%08X | data2\n", table.data2);
	IWX_ERR(sc, "%08X | data3\n", table.data3);
	IWX_ERR(sc, "%08X | beacon time\n", table.bcon_time);
	IWX_ERR(sc, "%08X | tsf low\n", table.tsf_low);
	IWX_ERR(sc, "%08X | tsf hi\n", table.tsf_hi);
	IWX_ERR(sc, "%08X | time gp1\n", table.gp1);
	IWX_ERR(sc, "%08X | time gp2\n", table.gp2);
	IWX_ERR(sc, "%08X | uCode revision type\n", table.fw_rev_type);
	IWX_ERR(sc, "%08X | uCode version major\n", table.major);
	IWX_ERR(sc, "%08X | uCode version minor\n", table.minor);
	IWX_ERR(sc, "%08X | hw version\n", table.hw_ver);
	IWX_ERR(sc, "%08X | board version\n", table.brd_ver);
	IWX_ERR(sc, "%08X | hcmd\n", table.hcmd);
	IWX_ERR(sc, "%08X | isr0\n", table.isr0);
	IWX_ERR(sc, "%08X | isr1\n", table.isr1);
	IWX_ERR(sc, "%08X | isr2\n", table.isr2);
	IWX_ERR(sc, "%08X | isr3\n", table.isr3);
	IWX_ERR(sc, "%08X | isr4\n", table.isr4);
	IWX_ERR(sc, "%08X | last cmd Id\n", table.last_cmd_id);
	IWX_ERR(sc, "%08X | wait_event\n", table.wait_event);
	IWX_ERR(sc, "%08X | l2p_control\n", table.l2p_control);
	IWX_ERR(sc, "%08X | l2p_duration\n", table.l2p_duration);
	IWX_ERR(sc, "%08X | l2p_mhvalid\n", table.l2p_mhvalid);
	IWX_ERR(sc, "%08X | l2p_addr_match\n", table.l2p_addr_match);
	IWX_ERR(sc, "%08X | lmpm_pmg_sel\n", table.lmpm_pmg_sel);
	IWX_ERR(sc, "%08X | timestamp\n", table.u_timestamp);
	IWX_ERR(sc, "%08X | flow_handler\n", table.flow_handler);

	if (sc->sc_uc.uc_umac_error_event_table)
		iwx_nic_umac_error(sc);
}

void
iwx_dump_error_log(struct iwx_softc *sc)
{
	if (sc->sc_flags & IWX_FLAG_FW_ERR)
		iwx_nic_error(sc);
}
