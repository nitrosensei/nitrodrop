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
 * airdrop-win port notes (driver/core/iwx_trans.c)
 *
 * PCIe transport for the AX210 family ("gen3" context info + IML), ported
 * from OpenBSD if_iwx.c rev 1.230. Function names and statement order are
 * kept 1:1 with iwx so the two can be diffed side by side. Only the
 * AX210-family branches are relevant for So+HR, but the 22000/BZ branches
 * of the shared helpers are kept to stay diff-able.
 *
 * Divergences from iwx (all intentional):
 *   D-TR1 bus_dma maps + mbufs -> osdep_dma_alloc() common buffers.
 *         RX buffers are fixed 4 KiB common buffers that are never swapped:
 *         received data is copied out (osdep_rx_frame), so the RX
 *         descriptor of a buffer never changes after ALIVE. iwx instead
 *         replaces the mbuf of an RB that carried an MPDU (iwx_rx_addbuf);
 *         descriptor contents stay identical, only the buffer address would
 *         differ.
 *   D-TR2 bus_dmamap_sync(PREWRITE/POSTREAD) -> osdep_barrier(): common
 *         buffers are cache coherent, only ordering against MMIO matters.
 *   D-TR3 iwx_intr()/iwx_intr_msix() are split into an ISR half (DIRQL,
 *         MMIO only) and a DPC half; see iwx_isr_* / iwx_dpc_*.
 *         task_add(systq, &sc->init_task) -> osdep_fatal(): the backend
 *         stops the device at PASSIVE_LEVEL and dumps the error log there,
 *         because iwx_nic_error() takes the NIC lock, which must never be
 *         taken from the DPC (it is held by the PASSIVE path across whole
 *         command sequences in iwx_init_hw()).
 *   D-TR4 only the command queue (txq[0]) is allocated at attach; data
 *         queues are allocated when the radio is started (M3). iwx
 *         allocates all IWX_NUM_TX_QUEUES rings in iwx_attach().
 *   D-TR5 iwx_stop_device(): net80211 BlockAck teardown and PNVM DMA frees
 *         removed (no BA, no PNVM for So+HR). Pending command response
 *         buffers are released (iwx relies on sc_generation for that).
 *   D-TR6 iwx_apm_config(): pci_conf_read via osdep_pci_read32().
 *   D-TR7 ICT is allocated and programmed exactly like iwx even in MSI-X
 *         mode (iwx_post_alive -> iwx_ict_reset in both modes).
 *   D-TR8 diagnostics only: in iwx_notif_intr the completion descriptor's
 *         rbid is compared with the ring index iwx assumes; mismatches are
 *         counted (rxq.rbid_mismatch), behaviour is unchanged.
 */

#include "iwx_var.h"

/* PCIe capability registers used by iwx_apm_config() (D-TR6). */
#define PCI_PCIE_LCSR		0x10
#define PCI_PCIE_LCSR_ASPM_L0S	0x00000001
#define PCI_PCIE_LCSR_ASPM_L1	0x00000002
#define PCI_PCIE_DCSR2		0x28
#define PCI_PCIE_DCSR2_LTREN	0x00000400

#define IWX_DMA_SYNC(sc)	osdep_barrier((sc)->sc_os)	/* D-TR2 */

static uint32_t
iwx_prph_addr_mask(struct iwx_softc *sc)
{
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
		return 0x00ffffff;
	else
		return 0x000fffff;
}

uint32_t
iwx_read_prph_unlocked(struct iwx_softc *sc, uint32_t addr)
{
	uint32_t mask = iwx_prph_addr_mask(sc);
	IWX_WRITE(sc, IWX_HBUS_TARG_PRPH_RADDR, ((addr & mask) | (3 << 24)));
	IWX_BARRIER_READ_WRITE(sc);
	return IWX_READ(sc, IWX_HBUS_TARG_PRPH_RDAT);
}

uint32_t
iwx_read_prph(struct iwx_softc *sc, uint32_t addr)
{
	iwx_nic_assert_locked(sc);
	return iwx_read_prph_unlocked(sc, addr);
}

void
iwx_write_prph_unlocked(struct iwx_softc *sc, uint32_t addr, uint32_t val)
{
	uint32_t mask = iwx_prph_addr_mask(sc);
	IWX_WRITE(sc, IWX_HBUS_TARG_PRPH_WADDR, ((addr & mask) | (3 << 24)));
	IWX_BARRIER_WRITE(sc);
	IWX_WRITE(sc, IWX_HBUS_TARG_PRPH_WDAT, val);
}

void
iwx_write_prph(struct iwx_softc *sc, uint32_t addr, uint32_t val)
{
	iwx_nic_assert_locked(sc);
	iwx_write_prph_unlocked(sc, addr, val);
}

uint32_t
iwx_read_umac_prph_unlocked(struct iwx_softc *sc, uint32_t addr)
{
	return iwx_read_prph_unlocked(sc, addr + (uint32_t)sc->sc_umac_prph_offset);
}

uint32_t
iwx_read_umac_prph(struct iwx_softc *sc, uint32_t addr)
{
	return iwx_read_prph(sc, addr + (uint32_t)sc->sc_umac_prph_offset);
}

void
iwx_write_umac_prph_unlocked(struct iwx_softc *sc, uint32_t addr, uint32_t val)
{
	iwx_write_prph_unlocked(sc, addr + (uint32_t)sc->sc_umac_prph_offset, val);
}

void
iwx_write_umac_prph(struct iwx_softc *sc, uint32_t addr, uint32_t val)
{
	iwx_write_prph(sc, addr + (uint32_t)sc->sc_umac_prph_offset, val);
}

int
iwx_read_mem(struct iwx_softc *sc, uint32_t addr, void *buf, int dwords)
{
	int offs, err = 0;
	uint32_t *vals = buf;

	if (iwx_nic_lock(sc)) {
		IWX_WRITE(sc, IWX_HBUS_TARG_MEM_RADDR, addr);
		for (offs = 0; offs < dwords; offs++)
			vals[offs] = le32toh(IWX_READ(sc, IWX_HBUS_TARG_MEM_RDAT));
		iwx_nic_unlock(sc);
	} else {
		err = EBUSY;
	}
	return err;
}

int
iwx_write_mem(struct iwx_softc *sc, uint32_t addr, const void *buf, int dwords)
{
	int offs;
	const uint32_t *vals = buf;

	if (iwx_nic_lock(sc)) {
		IWX_WRITE(sc, IWX_HBUS_TARG_MEM_WADDR, addr);
		/* WADDR auto-increments */
		for (offs = 0; offs < dwords; offs++) {
			uint32_t val = vals ? vals[offs] : 0;
			IWX_WRITE(sc, IWX_HBUS_TARG_MEM_WDAT, val);
		}
		iwx_nic_unlock(sc);
	} else {
		return EBUSY;
	}
	return 0;
}

int
iwx_poll_bit(struct iwx_softc *sc, int reg, uint32_t bits, uint32_t mask,
    int timo)
{
	for (;;) {
		if ((IWX_READ(sc, reg) & mask) == (bits & mask)) {
			return 1;
		}
		if (timo < 10) {
			return 0;
		}
		timo -= 10;
		DELAY(10);
	}
}

int
iwx_nic_lock(struct iwx_softc *sc)
{
	uint32_t access_req, ready, mask;

	if (sc->sc_nic_locks > 0) {
		iwx_nic_assert_locked(sc);
		sc->sc_nic_locks++;
		return 1; /* already locked */
	}

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		access_req = IWX_CSR_GP_CNTRL_REG_FLAG_BZ_MAC_ACCESS_REQ;
		ready = IWX_CSR_GP_CNTRL_REG_FLAG_MAC_STATUS;
		mask = IWX_CSR_GP_CNTRL_REG_FLAG_MAC_STATUS;
	} else {
		access_req = IWX_CSR_GP_CNTRL_REG_FLAG_MAC_ACCESS_REQ;
		ready = IWX_CSR_GP_CNTRL_REG_VAL_MAC_ACCESS_EN;
		mask = IWX_CSR_GP_CNTRL_REG_FLAG_MAC_CLOCK_READY |
		    IWX_CSR_GP_CNTRL_REG_FLAG_GOING_TO_SLEEP;
	}

	IWX_SETBITS(sc, IWX_CSR_GP_CNTRL, access_req);

	DELAY(2);

	if (iwx_poll_bit(sc, IWX_CSR_GP_CNTRL, ready, mask, 150000)) {
		sc->sc_nic_locks++;
		return 1;
	}

	IWX_ERR(sc, "acquiring device failed\n");
	return 0;
}

void
iwx_nic_assert_locked(struct iwx_softc *sc)
{
	/* iwx panics here; a driver bug, but never crash the machine. */
	if (sc->sc_nic_locks <= 0)
		IWX_ERR(sc, "BUG: nic locks counter %d\n", sc->sc_nic_locks);
}

void
iwx_nic_unlock(struct iwx_softc *sc)
{
	uint32_t access_req;

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ)
		access_req = IWX_CSR_GP_CNTRL_REG_FLAG_BZ_MAC_ACCESS_REQ;
	else
		access_req = IWX_CSR_GP_CNTRL_REG_FLAG_MAC_ACCESS_REQ;

	if (sc->sc_nic_locks > 0) {
		if (--sc->sc_nic_locks == 0)
			IWX_CLRBITS(sc, IWX_CSR_GP_CNTRL, access_req);
	} else
		IWX_ERR(sc, "NIC already unlocked\n");
}

int
iwx_set_bits_mask_prph(struct iwx_softc *sc, uint32_t reg, uint32_t bits,
    uint32_t mask)
{
	uint32_t val;

	if (iwx_nic_lock(sc)) {
		val = iwx_read_prph(sc, reg) & mask;
		val |= bits;
		iwx_write_prph(sc, reg, val);
		iwx_nic_unlock(sc);
		return 0;
	}
	return EBUSY;
}

int
iwx_set_bits_prph(struct iwx_softc *sc, uint32_t reg, uint32_t bits)
{
	return iwx_set_bits_mask_prph(sc, reg, bits, ~0u);
}

int
iwx_clear_bits_prph(struct iwx_softc *sc, uint32_t reg, uint32_t bits)
{
	return iwx_set_bits_mask_prph(sc, reg, 0, ~bits);
}

/* D-TR1 */
int
iwx_dma_contig_alloc(struct iwx_softc *sc, struct iwx_dma_info *dma,
    size_t size, size_t alignment)
{
	int err;

	memset(dma, 0, sizeof(*dma));
	err = osdep_dma_alloc(sc->sc_os, dma, size, alignment);
	if (err) {
		memset(dma, 0, sizeof(*dma));
		return err;
	}
	if (alignment > 1 && (dma->paddr & (alignment - 1)) != 0) {
		IWX_ERR(sc, "DMA buffer misaligned (0x%llx, align %u)\n",
		    (unsigned long long)dma->paddr, (unsigned)alignment);
		osdep_dma_free(sc->sc_os, dma);
		memset(dma, 0, sizeof(*dma));
		return ENOMEM;
	}
	return 0;
}

void
iwx_dma_contig_free(struct iwx_softc *sc, struct iwx_dma_info *dma)
{
	if (dma->vaddr != NULL)
		osdep_dma_free(sc->sc_os, dma);
	memset(dma, 0, sizeof(*dma));
}

void
iwx_update_rx_desc(struct iwx_softc *sc, struct iwx_rx_ring *ring, int idx)
{
	struct iwx_rx_data *data = &ring->data[idx];

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		struct iwx_rx_transfer_desc *desc = ring->desc;
		desc[idx].rbid = htole16(idx & 0xffff);
		desc[idx].addr = htole64(data->buf.paddr);
	} else {
		((uint64_t *)ring->desc)[idx] =
		    htole64(data->buf.paddr | (idx & 0x0fff));
	}
	IWX_DMA_SYNC(sc);
}

int
iwx_alloc_rx_ring(struct iwx_softc *sc, struct iwx_rx_ring *ring)
{
	size_t size;
	int i, err;

	ring->cur = 0;

	/* Allocate RX descriptors (256-byte aligned). */
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
		size = sizeof(struct iwx_rx_transfer_desc);
	else
		size = sizeof(uint64_t);
	err = iwx_dma_contig_alloc(sc, &ring->free_desc_dma,
	    size * IWX_RX_MQ_RING_COUNT, 256);
	if (err) {
		IWX_ERR(sc, "could not allocate RX ring DMA memory\n");
		goto fail;
	}
	ring->desc = ring->free_desc_dma.vaddr;

	/* Allocate RX status area (16-byte aligned). */
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
		size = sizeof(uint16_t);
	else
		size = sizeof(struct iwx_rb_status);
	err = iwx_dma_contig_alloc(sc, &ring->stat_dma, size, 16);
	if (err) {
		IWX_ERR(sc, "could not allocate RX status DMA memory\n");
		goto fail;
	}

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ)
		size = sizeof(struct iwx_rx_completion_desc_bz);
	else if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
		size = sizeof(struct iwx_rx_completion_desc);
	else
		size = sizeof(uint32_t);
	err = iwx_dma_contig_alloc(sc, &ring->used_desc_dma,
	    size * IWX_RX_MQ_RING_COUNT, 256);
	if (err) {
		IWX_ERR(sc, "could not allocate RX ring DMA memory\n");
		goto fail;
	}

	for (i = 0; i < IWX_RX_MQ_RING_COUNT; i++) {
		struct iwx_rx_data *data = &ring->data[i];

		/* D-TR1: iwx_rx_addbuf() with a fixed common buffer */
		err = iwx_dma_contig_alloc(sc, &data->buf, IWX_RBUF_SIZE,
		    IWX_RBUF_SIZE);
		if (err) {
			IWX_ERR(sc, "could not allocate RX buffer %d\n", i);
			goto fail;
		}
		iwx_update_rx_desc(sc, ring, i);
	}
	return 0;

fail:	iwx_free_rx_ring(sc, ring);
	return err;
}

void
iwx_disable_rx_dma(struct iwx_softc *sc)
{
	int ntries;

	if (iwx_nic_lock(sc)) {
		if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
			iwx_write_umac_prph(sc, IWX_RFH_RXF_DMA_CFG_GEN3, 0);
		else
			iwx_write_prph(sc, IWX_RFH_RXF_DMA_CFG, 0);
		for (ntries = 0; ntries < 1000; ntries++) {
			if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
				if (iwx_read_umac_prph(sc,
				    IWX_RFH_GEN_STATUS_GEN3) & IWX_RXF_DMA_IDLE)
					break;
			} else {
				if (iwx_read_prph(sc, IWX_RFH_GEN_STATUS) &
				    IWX_RXF_DMA_IDLE)
					break;
			}
			DELAY(10);
		}
		iwx_nic_unlock(sc);
	}
}

void
iwx_reset_rx_ring(struct iwx_softc *sc, struct iwx_rx_ring *ring)
{
	ring->cur = 0;
	if (ring->stat_dma.vaddr == NULL)
		return;
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		uint16_t *status = ring->stat_dma.vaddr;
		*status = 0;
	} else
		memset(ring->stat_dma.vaddr, 0, sizeof(struct iwx_rb_status));
	IWX_DMA_SYNC(sc);
}

void
iwx_free_rx_ring(struct iwx_softc *sc, struct iwx_rx_ring *ring)
{
	int i;

	iwx_dma_contig_free(sc, &ring->free_desc_dma);
	iwx_dma_contig_free(sc, &ring->stat_dma);
	iwx_dma_contig_free(sc, &ring->used_desc_dma);
	ring->desc = NULL;

	for (i = 0; i < IWX_RX_MQ_RING_COUNT; i++)
		iwx_dma_contig_free(sc, &ring->data[i].buf);
}

int
iwx_alloc_tx_ring(struct iwx_softc *sc, struct iwx_tx_ring *ring, int qid)
{
	uint64_t paddr;
	size_t size;
	int i, err;
	size_t bc_tbl_size;
	size_t bc_align;

	ring->qid = qid;
	ring->queued = 0;
	ring->cur = 0;
	ring->cur_hw = 0;
	ring->tail = 0;
	ring->tail_hw = 0;

	/* Allocate TX descriptors (256-byte aligned). */
	size = IWX_TX_RING_COUNT * sizeof(struct iwx_tfh_tfd);
	err = iwx_dma_contig_alloc(sc, &ring->desc_dma, size, 256);
	if (err) {
		IWX_ERR(sc, "could not allocate TX ring DMA memory\n");
		goto fail;
	}
	ring->desc = ring->desc_dma.vaddr;

	/*
	 * In DQA mode iwx uses 1 command queue + 1 default queue for
	 * management, control, and non-QoS data frames: txq[0] and txq[1].
	 */
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		bc_tbl_size = sizeof(struct iwx_gen3_bc_tbl_entry) *
		    IWX_TFD_QUEUE_BC_SIZE_GEN3_AX210;
		bc_align = 128;
	} else {
		bc_tbl_size = sizeof(struct iwx_agn_scd_bc_tbl);
		bc_align = 64;
	}
	err = iwx_dma_contig_alloc(sc, &ring->bc_tbl, bc_tbl_size, bc_align);
	if (err) {
		IWX_ERR(sc, "could not allocate byte count table DMA memory\n");
		goto fail;
	}

	size = IWX_TX_RING_COUNT * sizeof(struct iwx_device_cmd);
	err = iwx_dma_contig_alloc(sc, &ring->cmd_dma, size,
	    IWX_FIRST_TB_SIZE_ALIGN);
	if (err) {
		IWX_ERR(sc, "could not allocate cmd DMA memory\n");
		goto fail;
	}
	ring->cmd = ring->cmd_dma.vaddr;

	paddr = ring->cmd_dma.paddr;
	for (i = 0; i < IWX_TX_RING_COUNT; i++) {
		struct iwx_tx_data *data = &ring->data[i];

		data->cmd_paddr = paddr;
		data->flags = 0;
		data->done = 0;
		paddr += sizeof(struct iwx_device_cmd);
	}
	ring->allocated = 1;
	return 0;

fail:	iwx_free_tx_ring(sc, ring);
	return err;
}

void
iwx_reset_tx_ring(struct iwx_softc *sc, struct iwx_tx_ring *ring)
{
	int i;

	if (!ring->allocated)
		return;

	/* Clear byte count table. */
	memset(ring->bc_tbl.vaddr, 0, ring->bc_tbl.size);

	/* Clear TX descriptors. */
	memset(ring->desc, 0, ring->desc_dma.size);
	IWX_DMA_SYNC(sc);
	for (i = 0; i < IWX_TX_RING_COUNT; i++) {
		ring->data[i].flags = 0;
		ring->data[i].done = 0;
	}
	sc->qfullmsk &= ~(1 << ring->qid);
	sc->qenablemsk &= ~(1 << ring->qid);
	ring->queued = 0;
	ring->cur = 0;
	ring->cur_hw = 0;
	ring->tail = 0;
	ring->tail_hw = 0;
	ring->tid = 0;
}

void
iwx_free_tx_ring(struct iwx_softc *sc, struct iwx_tx_ring *ring)
{
	iwx_dma_contig_free(sc, &ring->desc_dma);
	iwx_dma_contig_free(sc, &ring->cmd_dma);
	iwx_dma_contig_free(sc, &ring->bc_tbl);
	ring->desc = NULL;
	ring->cmd = NULL;
	ring->allocated = 0;
}

void
iwx_enable_rfkill_int(struct iwx_softc *sc)
{
	if (!sc->sc_msix) {
		sc->sc_intmask = IWX_CSR_INT_BIT_RF_KILL;
		IWX_WRITE(sc, IWX_CSR_INT_MASK, sc->sc_intmask);
	} else {
		IWX_WRITE(sc, IWX_CSR_MSIX_FH_INT_MASK_AD,
		    sc->sc_fh_init_mask);
		IWX_WRITE(sc, IWX_CSR_MSIX_HW_INT_MASK_AD,
		    ~IWX_MSIX_HW_INT_CAUSES_REG_RF_KILL);
		sc->sc_hw_mask = IWX_MSIX_HW_INT_CAUSES_REG_RF_KILL;
	}

	IWX_SETBITS(sc, IWX_CSR_GP_CNTRL,
	    IWX_CSR_GP_CNTRL_REG_FLAG_RFKILL_WAKE_L1A_EN);
}

int
iwx_check_rfkill(struct iwx_softc *sc)
{
	uint32_t v;
	int rv;

	/*
	 * "documentation" is not really helpful here:
	 *  27:	HW_RF_KILL_SW
	 *	Indicates state of (platform's) hardware RF-Kill switch
	 *
	 * But apparently when it's off, it's on ...
	 */
	v = IWX_READ(sc, IWX_CSR_GP_CNTRL);
	rv = (v & IWX_CSR_GP_CNTRL_REG_FLAG_HW_RF_KILL_SW) == 0;
	if (rv) {
		sc->sc_flags |= IWX_FLAG_RFKILL;
	} else {
		sc->sc_flags &= ~IWX_FLAG_RFKILL;
	}

	return rv;
}

void
iwx_enable_interrupts(struct iwx_softc *sc)
{
	if (!sc->sc_msix) {
		sc->sc_intmask = IWX_CSR_INI_SET_MASK;
		IWX_WRITE(sc, IWX_CSR_INT_MASK, sc->sc_intmask);
	} else {
		/*
		 * fh/hw_mask keeps all the unmasked causes.
		 * Unlike msi, in msix cause is enabled when it is unset.
		 */
		sc->sc_hw_mask = sc->sc_hw_init_mask;
		sc->sc_fh_mask = sc->sc_fh_init_mask;
		IWX_WRITE(sc, IWX_CSR_MSIX_FH_INT_MASK_AD,
		    ~sc->sc_fh_mask);
		IWX_WRITE(sc, IWX_CSR_MSIX_HW_INT_MASK_AD,
		    ~sc->sc_hw_mask);
	}
}

void
iwx_enable_fwload_interrupt(struct iwx_softc *sc)
{
	if (!sc->sc_msix) {
		sc->sc_intmask = IWX_CSR_INT_BIT_ALIVE | IWX_CSR_INT_BIT_FH_RX;
		IWX_WRITE(sc, IWX_CSR_INT_MASK, sc->sc_intmask);
	} else {
		IWX_WRITE(sc, IWX_CSR_MSIX_HW_INT_MASK_AD,
		    ~IWX_MSIX_HW_INT_CAUSES_REG_ALIVE);
		sc->sc_hw_mask = IWX_MSIX_HW_INT_CAUSES_REG_ALIVE;
		/*
		 * Leave all the FH causes enabled to get the ALIVE
		 * notification.
		 */
		IWX_WRITE(sc, IWX_CSR_MSIX_FH_INT_MASK_AD,
		    ~sc->sc_fh_init_mask);
		sc->sc_fh_mask = sc->sc_fh_init_mask;
	}
}

void
iwx_restore_interrupts(struct iwx_softc *sc)
{
	IWX_WRITE(sc, IWX_CSR_INT_MASK, sc->sc_intmask);
}

void
iwx_disable_interrupts(struct iwx_softc *sc)
{
	if (!sc->sc_msix) {
		IWX_WRITE(sc, IWX_CSR_INT_MASK, 0);

		/* acknowledge all interrupts */
		IWX_WRITE(sc, IWX_CSR_INT, ~0u);
		IWX_WRITE(sc, IWX_CSR_FH_INT_STATUS, ~0u);
	} else {
		IWX_WRITE(sc, IWX_CSR_MSIX_FH_INT_MASK_AD,
		    sc->sc_fh_init_mask);
		IWX_WRITE(sc, IWX_CSR_MSIX_HW_INT_MASK_AD,
		    sc->sc_hw_init_mask);
	}
}

void
iwx_ict_reset(struct iwx_softc *sc)
{
	iwx_disable_interrupts(sc);

	memset(sc->ict_dma.vaddr, 0, IWX_ICT_SIZE);
	sc->ict_cur = 0;
	IWX_DMA_SYNC(sc);

	/* Set physical address of ICT (4KB aligned). */
	IWX_WRITE(sc, IWX_CSR_DRAM_INT_TBL_REG,
	    IWX_CSR_DRAM_INT_TBL_ENABLE
	    | IWX_CSR_DRAM_INIT_TBL_WRAP_CHECK
	    | IWX_CSR_DRAM_INIT_TBL_WRITE_POINTER
	    | (uint32_t)(sc->ict_dma.paddr >> IWX_ICT_PADDR_SHIFT));

	/* Switch to ICT interrupt mode in driver. */
	sc->sc_flags |= IWX_FLAG_USE_ICT;

	IWX_WRITE(sc, IWX_CSR_INT, ~0u);
	iwx_enable_interrupts(sc);
}

#define IWX_HW_READY_TIMEOUT 50
int
iwx_set_hw_ready(struct iwx_softc *sc)
{
	int ready;

	IWX_SETBITS(sc, IWX_CSR_HW_IF_CONFIG_REG,
	    IWX_CSR_HW_IF_CONFIG_REG_BIT_NIC_READY);

	ready = iwx_poll_bit(sc, IWX_CSR_HW_IF_CONFIG_REG,
	    IWX_CSR_HW_IF_CONFIG_REG_BIT_NIC_READY,
	    IWX_CSR_HW_IF_CONFIG_REG_BIT_NIC_READY,
	    IWX_HW_READY_TIMEOUT);
	if (ready)
		IWX_SETBITS(sc, IWX_CSR_MBOX_SET_REG,
		    IWX_CSR_MBOX_SET_REG_OS_ALIVE);

	return ready;
}
#undef IWX_HW_READY_TIMEOUT

int
iwx_prepare_card_hw(struct iwx_softc *sc)
{
	int t = 0;
	int ntries;

	if (iwx_set_hw_ready(sc))
		return 0;

	IWX_SETBITS(sc, IWX_CSR_DBG_LINK_PWR_MGMT_REG,
	    IWX_CSR_RESET_LINK_PWR_MGMT_DISABLED);
	DELAY(1000);

	for (ntries = 0; ntries < 10; ntries++) {
		/* If HW is not ready, prepare the conditions to check again */
		IWX_SETBITS(sc, IWX_CSR_HW_IF_CONFIG_REG,
		    IWX_CSR_HW_IF_CONFIG_REG_PREPARE);

		do {
			if (iwx_set_hw_ready(sc))
				return 0;
			DELAY(200);
			t += 200;
		} while (t < 150000);
		DELAY(25000);
	}

	return ETIMEDOUT;
}

int
iwx_force_power_gating(struct iwx_softc *sc)
{
	int err;

	err = iwx_set_bits_prph(sc, IWX_HPM_HIPM_GEN_CFG,
	    IWX_HPM_HIPM_GEN_CFG_CR_FORCE_ACTIVE);
	if (err)
		return err;
	DELAY(20);
	err = iwx_set_bits_prph(sc, IWX_HPM_HIPM_GEN_CFG,
	    IWX_HPM_HIPM_GEN_CFG_CR_PG_EN |
	    IWX_HPM_HIPM_GEN_CFG_CR_SLP_EN);
	if (err)
		return err;
	DELAY(20);
	err = iwx_clear_bits_prph(sc, IWX_HPM_HIPM_GEN_CFG,
	    IWX_HPM_HIPM_GEN_CFG_CR_FORCE_ACTIVE);
	return err;
}

void
iwx_apm_config(struct iwx_softc *sc)
{
	uint32_t lctl, cap;

	/*
	 * L0S states have been found to be unstable with our devices
	 * and in newer hardware they are not officially supported at
	 * all, so we must always set the L0S_DISABLED bit.
	 */
	IWX_SETBITS(sc, IWX_CSR_GIO_REG, IWX_CSR_GIO_REG_VAL_L0S_DISABLED);

	if (sc->sc_cap_off == 0) {
		/* no PCIe capability found: assume worst case */
		sc->sc_pm_support = 0;
		sc->sc_ltr_enabled = 0;
		return;
	}
	lctl = osdep_pci_read32(sc->sc_os,
	    (uint32_t)sc->sc_cap_off + PCI_PCIE_LCSR);
	sc->sc_pm_support = !(lctl & PCI_PCIE_LCSR_ASPM_L0S);
	cap = osdep_pci_read32(sc->sc_os,
	    (uint32_t)sc->sc_cap_off + PCI_PCIE_DCSR2);
	sc->sc_ltr_enabled = (cap & PCI_PCIE_DCSR2_LTREN) ? 1 : 0;
	DPRINTF(sc, "L1 %sabled - LTR %sabled\n",
	    (lctl & PCI_PCIE_LCSR_ASPM_L1) ? "En" : "Dis",
	    sc->sc_ltr_enabled ? "En" : "Dis");
}

/*
 * Start up NIC's basic functionality after it has been reset
 * e.g. after platform boot or shutdown.
 * NOTE:  This does not load uCode nor start the embedded processor
 */
int
iwx_apm_init(struct iwx_softc *sc)
{
	int err = 0;
	uint32_t ready;

	/*
	 * Disable L0s without affecting L1;
	 *  don't wait for ICH L0s (ICH bug W/A)
	 */
	IWX_SETBITS(sc, IWX_CSR_GIO_CHICKEN_BITS,
	    IWX_CSR_GIO_CHICKEN_BITS_REG_BIT_L1A_NO_L0S_RX);

	/* Set FH wait threshold to maximum (HW error during stress W/A) */
	IWX_SETBITS(sc, IWX_CSR_DBG_HPET_MEM_REG, IWX_CSR_DBG_HPET_MEM_REG_VAL);

	/*
	 * Enable HAP INTA (interrupt from management bus) to
	 * wake device's PCI Express link L1a -> L0s
	 */
	IWX_SETBITS(sc, IWX_CSR_HW_IF_CONFIG_REG,
	    IWX_CSR_HW_IF_CONFIG_REG_BIT_HAP_WAKE_L1A);

	iwx_apm_config(sc);

	/*
	 * Set "initialization complete" bit to move adapter from
	 * D0U* --> D0A* (powered-up active) state.
	 */
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		IWX_SETBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_MAC_CLOCK_READY |
		    IWX_CSR_GP_CNTRL_REG_FLAG_MAC_INIT);
		ready = IWX_CSR_GP_CNTRL_REG_FLAG_MAC_STATUS;
	} else {
		IWX_SETBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_INIT_DONE);
		ready = IWX_CSR_GP_CNTRL_REG_FLAG_MAC_CLOCK_READY;
	}

	/*
	 * Wait for clock stabilization; once stabilized, access to
	 * device-internal resources is supported, e.g. iwx_write_prph()
	 * and accesses to uCode SRAM.
	 */
	if (!iwx_poll_bit(sc, IWX_CSR_GP_CNTRL, ready, ready, 25000)) {
		IWX_ERR(sc, "timeout waiting for clock stabilization\n");
		err = ETIMEDOUT;
		goto out;
	}
 out:
	if (err)
		IWX_ERR(sc, "apm init error %d\n", err);
	return err;
}

void
iwx_apm_stop(struct iwx_softc *sc)
{
	IWX_SETBITS(sc, IWX_CSR_DBG_LINK_PWR_MGMT_REG,
	    IWX_CSR_RESET_LINK_PWR_MGMT_DISABLED);
	IWX_SETBITS(sc, IWX_CSR_HW_IF_CONFIG_REG,
	    IWX_CSR_HW_IF_CONFIG_REG_PREPARE |
	    IWX_CSR_HW_IF_CONFIG_REG_ENABLE_PME);
	DELAY(1000);
	IWX_CLRBITS(sc, IWX_CSR_DBG_LINK_PWR_MGMT_REG,
	    IWX_CSR_RESET_LINK_PWR_MGMT_DISABLED);
	DELAY(5000);

	/* stop device's busmaster DMA activity */
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		IWX_SETBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_BUS_MASTER_DISABLE_REQ);

		if (!iwx_poll_bit(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_BUS_MASTER_DISABLE_STATUS,
		    IWX_CSR_GP_CNTRL_REG_FLAG_BUS_MASTER_DISABLE_STATUS, 5000))
			IWX_ERR(sc, "timeout waiting for master\n");

		DELAY(20000);
	} else {
		IWX_SETBITS(sc, IWX_CSR_RESET,
		    IWX_CSR_RESET_REG_FLAG_STOP_MASTER);

		if (!iwx_poll_bit(sc, IWX_CSR_RESET,
		    IWX_CSR_RESET_REG_FLAG_MASTER_DISABLED,
		    IWX_CSR_RESET_REG_FLAG_MASTER_DISABLED, 100))
			IWX_ERR(sc, "timeout waiting for master\n");
	}

	/*
	 * Clear "initialization complete" bit to move adapter from
	 * D0A* (powered-up Active) --> D0U* (Uninitialized) state.
	 */
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		IWX_CLRBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_MAC_INIT);
	} else {
		IWX_CLRBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_INIT_DONE);
	}
}

void
iwx_init_msix_hw(struct iwx_softc *sc)
{
	iwx_conf_msix_hw(sc, 0);

	if (!sc->sc_msix)
		return;

	sc->sc_fh_init_mask = ~IWX_READ(sc, IWX_CSR_MSIX_FH_INT_MASK_AD);
	sc->sc_fh_mask = sc->sc_fh_init_mask;
	sc->sc_hw_init_mask = ~IWX_READ(sc, IWX_CSR_MSIX_HW_INT_MASK_AD);
	sc->sc_hw_mask = sc->sc_hw_init_mask;
}

void
iwx_conf_msix_hw(struct iwx_softc *sc, int stopped)
{
	int vector = 0;

	if (!sc->sc_msix) {
		/* Newer chips default to MSIX. */
		if (!stopped && iwx_nic_lock(sc)) {
			iwx_write_umac_prph(sc, IWX_UREG_CHICK,
			    IWX_UREG_CHICK_MSI_ENABLE);
			iwx_nic_unlock(sc);
		}
		return;
	}

	if (!stopped && iwx_nic_lock(sc)) {
		iwx_write_umac_prph(sc, IWX_UREG_CHICK,
		    IWX_UREG_CHICK_MSIX_ENABLE);
		iwx_nic_unlock(sc);
	}

	/* Disable all interrupts */
	IWX_WRITE(sc, IWX_CSR_MSIX_FH_INT_MASK_AD, ~0u);
	IWX_WRITE(sc, IWX_CSR_MSIX_HW_INT_MASK_AD, ~0u);

	/* Map fallback-queue (command/mgmt) to a single vector */
	IWX_WRITE_1(sc, IWX_CSR_MSIX_RX_IVAR(0),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	/* Map RSS queue (data) to the same vector */
	IWX_WRITE_1(sc, IWX_CSR_MSIX_RX_IVAR(1),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);

	/* Enable the RX queues cause interrupts */
	IWX_CLRBITS(sc, IWX_CSR_MSIX_FH_INT_MASK_AD,
	    IWX_MSIX_FH_INT_CAUSES_Q0 | IWX_MSIX_FH_INT_CAUSES_Q1);

	/* Map non-RX causes to the same vector */
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_D2S_CH0_NUM),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_D2S_CH1_NUM),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_S2D),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_FH_ERR),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_ALIVE),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_WAKEUP),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_RESET_DONE),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_CT_KILL),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_RF_KILL),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_PERIODIC),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_SW_ERR),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_SW_ERR_V2),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_SCD),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_FH_TX),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_HW_ERR),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);
	IWX_WRITE_1(sc, IWX_CSR_MSIX_IVAR(IWX_MSIX_IVAR_CAUSE_REG_HAP),
	    vector | IWX_MSIX_NON_AUTO_CLEAR_CAUSE);

	/* Enable non-RX causes interrupts */
	IWX_CLRBITS(sc, IWX_CSR_MSIX_FH_INT_MASK_AD,
	    IWX_MSIX_FH_INT_CAUSES_D2S_CH0_NUM |
	    IWX_MSIX_FH_INT_CAUSES_D2S_CH1_NUM |
	    IWX_MSIX_FH_INT_CAUSES_S2D |
	    IWX_MSIX_FH_INT_CAUSES_FH_ERR);
	IWX_CLRBITS(sc, IWX_CSR_MSIX_HW_INT_MASK_AD,
	    IWX_MSIX_HW_INT_CAUSES_REG_ALIVE |
	    IWX_MSIX_HW_INT_CAUSES_REG_WAKEUP |
	    IWX_MSIX_HW_INT_CAUSES_REG_RESET_DONE |
	    IWX_MSIX_HW_INT_CAUSES_REG_CT_KILL |
	    IWX_MSIX_HW_INT_CAUSES_REG_RF_KILL |
	    IWX_MSIX_HW_INT_CAUSES_REG_PERIODIC |
	    IWX_MSIX_HW_INT_CAUSES_REG_SW_ERR |
	    IWX_MSIX_HW_INT_CAUSES_REG_SW_ERR_V2 |
	    IWX_MSIX_HW_INT_CAUSES_REG_SCD |
	    IWX_MSIX_HW_INT_CAUSES_REG_FH_TX |
	    IWX_MSIX_HW_INT_CAUSES_REG_HW_ERR |
	    IWX_MSIX_HW_INT_CAUSES_REG_HAP);
}

int
iwx_clear_persistence_bit(struct iwx_softc *sc)
{
	uint32_t hpm, wprot;

	hpm = iwx_read_prph_unlocked(sc, IWX_HPM_DEBUG);
	if (hpm != 0xa5a5a5a0 && (hpm & IWX_PERSISTENCE_BIT)) {
		wprot = iwx_read_prph_unlocked(sc, IWX_PREG_PRPH_WPROT_22000);
		if (wprot & IWX_PREG_WFPM_ACCESS) {
			IWX_ERR(sc, "cannot clear persistence bit\n");
			return EPERM;
		}
		iwx_write_prph_unlocked(sc, IWX_HPM_DEBUG,
		    hpm & ~IWX_PERSISTENCE_BIT);
	}

	return 0;
}

void
iwx_sw_reset(struct iwx_softc *sc)
{
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		IWX_SETBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_SW_RESET);
		DELAY(20000);
	} else {
		IWX_SETBITS(sc, IWX_CSR_RESET, IWX_CSR_RESET_REG_FLAG_SW_RESET);
		DELAY(5000);
	}
}

int
iwx_start_hw(struct iwx_softc *sc)
{
	int err;

	err = iwx_prepare_card_hw(sc);
	if (err)
		return err;

	if (sc->sc_device_family == IWX_DEVICE_FAMILY_22000) {
		err = iwx_clear_persistence_bit(sc);
		if (err)
			return err;
	}

	/* Reset the entire device */
	iwx_sw_reset(sc);

	if (sc->sc_device_family == IWX_DEVICE_FAMILY_22000 &&
	    sc->sc_integrated) {
		IWX_SETBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_INIT_DONE);
		DELAY(20);
		if (!iwx_poll_bit(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_MAC_CLOCK_READY,
		    IWX_CSR_GP_CNTRL_REG_FLAG_MAC_CLOCK_READY, 25000)) {
			IWX_ERR(sc, "timeout waiting for clock stabilization\n");
			return ETIMEDOUT;
		}

		err = iwx_force_power_gating(sc);
		if (err)
			return err;

		/* Reset the entire device */
		iwx_sw_reset(sc);
	}

	err = iwx_apm_init(sc);
	if (err)
		return err;

	iwx_init_msix_hw(sc);

	iwx_enable_rfkill_int(sc);
	iwx_check_rfkill(sc);

	return 0;
}

void
iwx_stop_device(struct iwx_softc *sc)
{
	int i;

	iwx_disable_interrupts(sc);
	sc->sc_flags &= ~IWX_FLAG_USE_ICT;

	iwx_disable_rx_dma(sc);
	iwx_reset_rx_ring(sc, &sc->rxq);
	osdep_lock(sc->sc_os);
	for (i = 0; i < (int)nitems(sc->txq); i++)
		iwx_reset_tx_ring(sc, &sc->txq[i]);
	/* D-TR5: drop response buffers of commands that will never finish */
	for (i = 0; i < IWX_TX_RING_COUNT; i++) {
		if (sc->sc_cmd_resp_pkt[i] != NULL) {
			osdep_free(sc->sc_cmd_resp_pkt[i],
			    sc->sc_cmd_resp_len[i]);
			sc->sc_cmd_resp_pkt[i] = NULL;
		}
	}
	sc->cmd_big_idx = -1;
	osdep_unlock(sc->sc_os);

	/* Make sure (redundant) we've released our request to stay awake */
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		IWX_CLRBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_BZ_MAC_ACCESS_REQ);
	} else {
		IWX_CLRBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_MAC_ACCESS_REQ);
	}
	if (sc->sc_nic_locks > 0)
		IWX_ERR(sc, "%d active NIC locks forcefully cleared\n",
		    sc->sc_nic_locks);
	sc->sc_nic_locks = 0;

	/* Stop the device, and put it in low power state */
	iwx_apm_stop(sc);

	/* Reset the on-board processor. */
	iwx_sw_reset(sc);

	/*
	 * Upon stop, the IVAR table gets erased, so msi-x won't
	 * work. This causes a bug in RF-KILL flows, since the interrupt
	 * that enables radio won't fire on the correct irq, and the
	 * driver won't be able to handle the interrupt.
	 * Configure the IVAR table again after reset.
	 */
	iwx_conf_msix_hw(sc, 1);

	/*
	 * Upon stop, the APM issues an interrupt if HW RF kill is set.
	 * Clear the interrupt again.
	 */
	iwx_disable_interrupts(sc);

	/* Even though we stop the HW we still want the RF kill interrupt. */
	iwx_enable_rfkill_int(sc);
	iwx_check_rfkill(sc);

	iwx_prepare_card_hw(sc);

	iwx_ctxt_info_free_paging(sc);
}

void
iwx_nic_config(struct iwx_softc *sc)
{
	uint32_t radio_cfg_type, radio_cfg_step, radio_cfg_dash;
	uint32_t mask, val, reg_val = 0;
	uint32_t phy = (uint32_t)sc->sc_fw_phy_config;

	radio_cfg_type = (phy & IWX_FW_PHY_CFG_RADIO_TYPE) >>
	    IWX_FW_PHY_CFG_RADIO_TYPE_POS;
	radio_cfg_step = (phy & IWX_FW_PHY_CFG_RADIO_STEP) >>
	    IWX_FW_PHY_CFG_RADIO_STEP_POS;
	radio_cfg_dash = (phy & IWX_FW_PHY_CFG_RADIO_DASH) >>
	    IWX_FW_PHY_CFG_RADIO_DASH_POS;

	reg_val |= IWX_CSR_HW_REV_STEP(sc->sc_hw_rev) <<
	    IWX_CSR_HW_IF_CONFIG_REG_POS_MAC_STEP;
	reg_val |= IWX_CSR_HW_REV_DASH(sc->sc_hw_rev) <<
	    IWX_CSR_HW_IF_CONFIG_REG_POS_MAC_DASH;

	/* radio configuration */
	reg_val |= radio_cfg_type << IWX_CSR_HW_IF_CONFIG_REG_POS_PHY_TYPE;
	reg_val |= radio_cfg_step << IWX_CSR_HW_IF_CONFIG_REG_POS_PHY_STEP;
	reg_val |= radio_cfg_dash << IWX_CSR_HW_IF_CONFIG_REG_POS_PHY_DASH;

	mask = IWX_CSR_HW_IF_CONFIG_REG_MSK_MAC_DASH |
	    IWX_CSR_HW_IF_CONFIG_REG_MSK_MAC_STEP |
	    IWX_CSR_HW_IF_CONFIG_REG_MSK_PHY_STEP |
	    IWX_CSR_HW_IF_CONFIG_REG_MSK_PHY_DASH |
	    IWX_CSR_HW_IF_CONFIG_REG_MSK_PHY_TYPE |
	    IWX_CSR_HW_IF_CONFIG_REG_BIT_RADIO_SI |
	    IWX_CSR_HW_IF_CONFIG_REG_BIT_MAC_SI;

	val = IWX_READ(sc, IWX_CSR_HW_IF_CONFIG_REG);
	val &= ~mask;
	val |= reg_val;
	IWX_WRITE(sc, IWX_CSR_HW_IF_CONFIG_REG, val);
}

int
iwx_nic_rx_init(struct iwx_softc *sc)
{
	IWX_WRITE_1(sc, IWX_CSR_INT_COALESCING, IWX_HOST_INT_TIMEOUT_DEF);

	/*
	 * We don't configure the RFH; the firmware will do that.
	 * Rx descriptors are set when firmware sends an ALIVE interrupt.
	 */
	return 0;
}

int
iwx_nic_init(struct iwx_softc *sc)
{
	int err;

	iwx_apm_init(sc);
	if (sc->sc_device_family < IWX_DEVICE_FAMILY_AX210)
		iwx_nic_config(sc);

	err = iwx_nic_rx_init(sc);
	if (err)
		return err;

	IWX_SETBITS(sc, IWX_CSR_MAC_SHADOW_REG_CTRL, 0x800fffff);

	return 0;
}

void
iwx_set_ltr(struct iwx_softc *sc)
{
	uint32_t ltr_val = IWX_CSR_LTR_LONG_VAL_AD_NO_SNOOP_REQ |
	    ((IWX_CSR_LTR_LONG_VAL_AD_SCALE_USEC <<
	    IWX_CSR_LTR_LONG_VAL_AD_NO_SNOOP_SCALE_SHIFT) &
	    IWX_CSR_LTR_LONG_VAL_AD_NO_SNOOP_SCALE_MASK) |
	    ((250 << IWX_CSR_LTR_LONG_VAL_AD_NO_SNOOP_VAL_SHIFT) &
	    IWX_CSR_LTR_LONG_VAL_AD_NO_SNOOP_VAL_MASK) |
	    IWX_CSR_LTR_LONG_VAL_AD_SNOOP_REQ |
	    ((IWX_CSR_LTR_LONG_VAL_AD_SCALE_USEC <<
	    IWX_CSR_LTR_LONG_VAL_AD_SNOOP_SCALE_SHIFT) &
	    IWX_CSR_LTR_LONG_VAL_AD_SNOOP_SCALE_MASK) |
	    (250 & IWX_CSR_LTR_LONG_VAL_AD_SNOOP_VAL);

	/*
	 * To workaround hardware latency issues during the boot process,
	 * initialize the LTR to ~250 usec (see ltr_val above).
	 * The firmware initializes this again later (to a smaller value).
	 */
	if (!sc->sc_integrated) {
		IWX_WRITE(sc, IWX_CSR_LTR_LONG_VAL_AD, ltr_val);
	} else if (sc->sc_integrated &&
		   sc->sc_device_family == IWX_DEVICE_FAMILY_22000) {
		iwx_write_prph(sc, IWX_HPM_MAC_LTR_CSR,
		    IWX_HPM_MAC_LRT_ENABLE_ALL);
		iwx_write_prph(sc, IWX_HPM_UMAC_LTR, ltr_val);
	}
}

static int
iwx_ctxt_info_alloc_dma(struct iwx_softc *sc,
    const struct iwx_fw_onesect *sec, struct iwx_dma_info *dram)
{
	int err = iwx_dma_contig_alloc(sc, dram, sec->fws_len, 0);
	if (err) {
		IWX_ERR(sc, "could not allocate context info DMA memory\n");
		return err;
	}

	memcpy(dram->vaddr, sec->fws_data, sec->fws_len);

	return 0;
}

void
iwx_ctxt_info_free_paging(struct iwx_softc *sc)
{
	struct iwx_self_init_dram *dram = &sc->init_dram;
	int i;

	if (!dram->paging)
		return;

	/* free paging*/
	for (i = 0; i < dram->paging_cnt; i++)
		iwx_dma_contig_free(sc, &dram->paging[i]);

	osdep_free(dram->paging, (size_t)dram->paging_cnt * sizeof(*dram->paging));
	dram->paging_cnt = 0;
	dram->paging = NULL;
}

int
iwx_init_fw_sec(struct iwx_softc *sc, const struct iwx_fw_sects *fws,
    struct iwx_context_info_dram *ctxt_dram)
{
	struct iwx_self_init_dram *dram = &sc->init_dram;
	int i, ret, fw_cnt = 0;

	if (dram->paging != NULL) {	/* iwx: KASSERT(dram->paging == NULL) */
		IWX_ERR(sc, "BUG: firmware paging memory still allocated\n");
		return EBUSY;
	}

	dram->lmac_cnt = iwx_get_num_sections(fws, 0);
	/* add 1 due to separator */
	dram->umac_cnt = iwx_get_num_sections(fws, dram->lmac_cnt + 1);
	/* add 2 due to separators */
	dram->paging_cnt = iwx_get_num_sections(fws,
	    dram->lmac_cnt + dram->umac_cnt + 2);

	/* the context info DRAM table has fixed-size arrays */
	if (dram->lmac_cnt > (int)nitems(ctxt_dram->lmac_img) ||
	    dram->umac_cnt > (int)nitems(ctxt_dram->umac_img) ||
	    dram->paging_cnt > (int)nitems(ctxt_dram->virtual_img)) {
		IWX_ERR(sc, "too many firmware sections %d/%d/%d\n",
		    dram->lmac_cnt, dram->umac_cnt, dram->paging_cnt);
		dram->lmac_cnt = dram->umac_cnt = dram->paging_cnt = 0;
		return EINVAL;
	}

	dram->fw = osdep_malloc((size_t)(dram->umac_cnt + dram->lmac_cnt) *
	    sizeof(*dram->fw) + 1);
	if (!dram->fw) {
		IWX_ERR(sc, "could not allocate memory for firmware sections\n");
		return ENOMEM;
	}

	dram->paging = osdep_malloc((size_t)dram->paging_cnt *
	    sizeof(*dram->paging) + 1);
	if (!dram->paging) {
		IWX_ERR(sc, "could not allocate memory for firmware paging\n");
		return ENOMEM;
	}

	/* initialize lmac sections */
	for (i = 0; i < dram->lmac_cnt; i++) {
		ret = iwx_ctxt_info_alloc_dma(sc, &fws->fw_sect[i],
						   &dram->fw[fw_cnt]);
		if (ret)
			return ret;
		ctxt_dram->lmac_img[i] =
			htole64(dram->fw[fw_cnt].paddr);
		DPRINTF(sc, "firmware LMAC section %d at 0x%llx size %u\n", i,
		    (unsigned long long)dram->fw[fw_cnt].paddr,
		    (unsigned)dram->fw[fw_cnt].size);
		fw_cnt++;
	}

	/* initialize umac sections */
	for (i = 0; i < dram->umac_cnt; i++) {
		/* access FW with +1 to make up for lmac separator */
		ret = iwx_ctxt_info_alloc_dma(sc,
		    &fws->fw_sect[fw_cnt + 1], &dram->fw[fw_cnt]);
		if (ret)
			return ret;
		ctxt_dram->umac_img[i] =
			htole64(dram->fw[fw_cnt].paddr);
		DPRINTF(sc, "firmware UMAC section %d at 0x%llx size %u\n", i,
		    (unsigned long long)dram->fw[fw_cnt].paddr,
		    (unsigned)dram->fw[fw_cnt].size);
		fw_cnt++;
	}

	/*
	 * Initialize paging.
	 * Paging memory isn't stored in dram->fw as the umac and lmac - it is
	 * stored separately.
	 * This is since the timing of its release is different -
	 * while fw memory can be released on alive, the paging memory can be
	 * freed only when the device goes down.
	 * Given that, the logic here in accessing the fw image is a bit
	 * different - fw_cnt isn't changing so loop counter is added to it.
	 */
	for (i = 0; i < dram->paging_cnt; i++) {
		/* access FW with +2 to make up for lmac & umac separators */
		int fw_idx = fw_cnt + i + 2;

		ret = iwx_ctxt_info_alloc_dma(sc,
		    &fws->fw_sect[fw_idx], &dram->paging[i]);
		if (ret)
			return ret;

		ctxt_dram->virtual_img[i] = htole64(dram->paging[i].paddr);
		DPRINTF(sc, "firmware paging section %d at 0x%llx size %u\n", i,
		    (unsigned long long)dram->paging[i].paddr,
		    (unsigned)dram->paging[i].size);
	}

	return 0;
}

void
iwx_ctxt_info_free_fw_img(struct iwx_softc *sc)
{
	struct iwx_self_init_dram *dram = &sc->init_dram;
	int i;

	if (!dram->fw)
		return;

	for (i = 0; i < dram->lmac_cnt + dram->umac_cnt; i++)
		iwx_dma_contig_free(sc, &dram->fw[i]);

	osdep_free(dram->fw,
	    (size_t)(dram->lmac_cnt + dram->umac_cnt) * sizeof(*dram->fw) + 1);
	dram->lmac_cnt = 0;
	dram->umac_cnt = 0;
	dram->fw = NULL;
}

int
iwx_ctxt_info_gen3_init(struct iwx_softc *sc, const struct iwx_fw_sects *fws)
{
	struct iwx_context_info_gen3 *ctxt_info_gen3;
	struct iwx_prph_scratch *prph_scratch;
	struct iwx_prph_scratch_ctrl_cfg *prph_sc_ctrl;
	uint16_t cb_size;
	uint32_t control_flags, scratch_size;
	uint64_t paddr;
	int err;

	if (sc->sc_fw.iml == NULL || sc->sc_fw.iml_len == 0) {
		IWX_ERR(sc, "no image loader found in firmware file\n");
		iwx_ctxt_info_free_fw_img(sc);
		return EINVAL;
	}

	err = iwx_dma_contig_alloc(sc, &sc->iml_dma,
	    sc->sc_fw.iml_len, 0);
	if (err) {
		IWX_ERR(sc, "could not allocate DMA memory for "
		    "firmware image loader\n");
		iwx_ctxt_info_free_fw_img(sc);
		return ENOMEM;
	}

	prph_scratch = sc->prph_scratch_dma.vaddr;
	memset(prph_scratch, 0, sizeof(*prph_scratch));
	prph_sc_ctrl = &prph_scratch->ctrl_cfg;
	prph_sc_ctrl->version.version = 0;
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ)
		prph_sc_ctrl->version.mac_id = htole16((uint16_t)sc->sc_hw_rev);
	else {
		prph_sc_ctrl->version.mac_id = htole16((uint16_t)IWX_READ(sc,
		    IWX_CSR_HW_REV));
	}
	prph_sc_ctrl->version.size = htole16(sizeof(*prph_scratch) / 4);

	control_flags = IWX_PRPH_SCRATCH_RB_SIZE_4K |
	    IWX_PRPH_SCRATCH_MTR_MODE |
	    (IWX_PRPH_MTR_FORMAT_256B & IWX_PRPH_SCRATCH_MTR_FORMAT);
	if (sc->sc_imr_enabled)
		control_flags |= IWX_PRPH_SCRATCH_IMR_DEBUG_EN;
	prph_sc_ctrl->control.control_flags = htole32(control_flags);

	/* initialize RX default queue */
	prph_sc_ctrl->rbd_cfg.free_rbd_addr =
	    htole64(sc->rxq.free_desc_dma.paddr);

	/* allocate ucode sections in dram and set addresses */
	err = iwx_init_fw_sec(sc, fws, &prph_scratch->dram);
	if (err) {
		iwx_dma_contig_free(sc, &sc->iml_dma);
		iwx_ctxt_info_free_fw_img(sc);
		iwx_ctxt_info_free_paging(sc);
		return err;
	}

	ctxt_info_gen3 = sc->ctxt_info_dma.vaddr;
	memset(ctxt_info_gen3, 0, sizeof(*ctxt_info_gen3));
	ctxt_info_gen3->prph_info_base_addr = htole64(sc->prph_info_dma.paddr);
	ctxt_info_gen3->prph_scratch_base_addr =
	    htole64(sc->prph_scratch_dma.paddr);
	scratch_size = sizeof(*prph_scratch);
	ctxt_info_gen3->prph_scratch_size = htole32(scratch_size);
	ctxt_info_gen3->cr_head_idx_arr_base_addr =
	    htole64(sc->rxq.stat_dma.paddr);
	ctxt_info_gen3->tr_tail_idx_arr_base_addr =
	    htole64(sc->prph_info_dma.paddr + IWX_PAGE_SIZE / 2);
	ctxt_info_gen3->cr_tail_idx_arr_base_addr =
	    htole64(sc->prph_info_dma.paddr + 3 * IWX_PAGE_SIZE / 4);
	ctxt_info_gen3->mtr_base_addr =
	    htole64(sc->txq[IWX_DQA_CMD_QUEUE].desc_dma.paddr);
	ctxt_info_gen3->mcr_base_addr = htole64(sc->rxq.used_desc_dma.paddr);
	cb_size = (uint16_t)IWX_TFD_QUEUE_CB_SIZE(IWX_TX_RING_COUNT);
	ctxt_info_gen3->mtr_size = htole16(cb_size);
	cb_size = (uint16_t)IWX_RX_QUEUE_CB_SIZE(IWX_MQ_RX_TABLE_SIZE);
	ctxt_info_gen3->mcr_size = htole16(cb_size);

	memcpy(sc->iml_dma.vaddr, sc->sc_fw.iml, sc->sc_fw.iml_len);
	IWX_DMA_SYNC(sc);

	paddr = sc->ctxt_info_dma.paddr;
	IWX_WRITE(sc, IWX_CSR_CTXT_INFO_ADDR, paddr & 0xffffffff);
	IWX_WRITE(sc, IWX_CSR_CTXT_INFO_ADDR + 4, paddr >> 32);

	paddr = sc->iml_dma.paddr;
	IWX_WRITE(sc, IWX_CSR_IML_DATA_ADDR, paddr & 0xffffffff);
	IWX_WRITE(sc, IWX_CSR_IML_DATA_ADDR + 4, paddr >> 32);
	IWX_WRITE(sc, IWX_CSR_IML_SIZE_ADDR, sc->sc_fw.iml_len);

	IWX_SETBITS(sc, IWX_CSR_CTXT_INFO_BOOT_CTRL,
		    IWX_CSR_AUTO_FUNC_BOOT_ENA);

	/* kick FW self load */
	if (!iwx_nic_lock(sc)) {
		iwx_dma_contig_free(sc, &sc->iml_dma);
		iwx_ctxt_info_free_fw_img(sc);
		return EBUSY;
	}
	iwx_set_ltr(sc);
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		IWX_WRITE(sc, IWX_CSR_FUNC_SCRATCH,
		    IWX_CSR_FUNC_SCRATCH_INIT_VALUE);
		IWX_SETBITS(sc, IWX_CSR_GP_CNTRL,
		    IWX_CSR_GP_CNTRL_REG_FLAG_ROM_START);
	} else
		iwx_write_umac_prph(sc, IWX_UREG_CPU_INIT_RUN, 1);
	iwx_nic_unlock(sc);

	/* Context info will be released upon alive or failure to get one */
	return 0;
}

int
iwx_load_firmware(struct iwx_softc *sc)
{
	struct iwx_fw_sects *fws;
	int err;

	sc->sc_uc.uc_intr = 0;
	sc->sc_uc.uc_ok = 0;
	osdep_event_clear(sc->sc_os, OSDEP_EV_ALIVE);

	fws = &sc->sc_fw.fw_sects[IWX_UCODE_TYPE_REGULAR];
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
		err = iwx_ctxt_info_gen3_init(sc, fws);
	else
		err = ENOTSUP;	/* pre-AX210 iwx_ctxt_info_init() not ported */
	if (err) {
		IWX_ERR(sc, "could not init context info\n");
		return err;
	}

	/* wait for the firmware to load (iwx: tsleep 1 second) */
	err = iwx_wait_cond(sc, OSDEP_EV_ALIVE, &sc->sc_uc.uc_intr, 1, 1000);
	if (err || !sc->sc_uc.uc_ok) {
		IWX_ERR(sc, "could not load firmware, %d\n", err);
		iwx_ctxt_info_free_paging(sc);
	}

	iwx_dma_contig_free(sc, &sc->iml_dma);
	iwx_ctxt_info_free_fw_img(sc);

	if (!sc->sc_uc.uc_ok)
		return EINVAL;

	return err;
}

int
iwx_start_fw(struct iwx_softc *sc)
{
	int err;

	IWX_WRITE(sc, IWX_CSR_INT, ~0u);

	iwx_disable_interrupts(sc);

	/* make sure rfkill handshake bits are cleared */
	IWX_WRITE(sc, IWX_CSR_UCODE_DRV_GP1_CLR, IWX_CSR_UCODE_SW_BIT_RFKILL);
	IWX_WRITE(sc, IWX_CSR_UCODE_DRV_GP1_CLR,
	    IWX_CSR_UCODE_DRV_GP1_BIT_CMD_BLOCKED);

	/* clear (again), then enable firmware load interrupt */
	IWX_WRITE(sc, IWX_CSR_INT, ~0u);

	err = iwx_nic_init(sc);
	if (err) {
		IWX_ERR(sc, "unable to init nic\n");
		return err;
	}

	iwx_enable_fwload_interrupt(sc);

	return iwx_load_firmware(sc);
}

void
iwx_get_crf_id(struct iwx_softc *sc)
{
	uint32_t val = 0;
	uint8_t step = 0;

	/* Enable access to peripheral registers */
	val = iwx_read_umac_prph_unlocked(sc, IWX_WFPM_CTRL_REG);
	val |= IWX_WFPM_AUX_CTL_AUX_IF_MAC_OWNER_MSK;
	iwx_write_umac_prph_unlocked(sc, IWX_WFPM_CTRL_REG, val);

	/* Read crf info */
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210)
		sc->sc_hw_crf_id = (int)iwx_read_prph_unlocked(sc, IWX_SD_REG_VER_GEN2);
	else
		sc->sc_hw_crf_id = (int)iwx_read_prph_unlocked(sc, IWX_SD_REG_VER);

	/* Read cnv info */
	sc->sc_hw_cnv_id = (int)iwx_read_prph_unlocked(sc, IWX_CNVI_AUX_MISC_CHIP);

	/* For BZ-W, take B step also when A step is indicated */
	if (IWX_CSR_HW_REV_TYPE(sc->sc_hw_rev) == IWX_CFG_MAC_TYPE_BZ_W)
		step = IWX_SILICON_B_STEP;

	/* In BZ, the MAC step must be read from the CNVI aux register */
	if (IWX_CSR_HW_REV_TYPE(sc->sc_hw_rev) == IWX_CFG_MAC_TYPE_BZ) {
		step = (uint8_t)IWX_CNVI_AUX_MISC_CHIP_MAC_STEP(sc->sc_hw_cnv_id);

		/* For BZ-U, take B step also when A step is indicated */
		if (IWX_CNVI_AUX_MISC_CHIP_PROD_TYPE(sc->sc_hw_cnv_id) ==
		    IWX_CNVI_AUX_MISC_CHIP_PROD_TYPE_BZ_U &&
		    step == IWX_SILICON_A_STEP)
			step = IWX_SILICON_B_STEP;
	}

	if (IWX_CSR_HW_REV_TYPE(sc->sc_hw_rev) == IWX_CFG_MAC_TYPE_BZ ||
	    IWX_CSR_HW_REV_TYPE(sc->sc_hw_rev) == IWX_CFG_MAC_TYPE_BZ_W)
		sc->sc_hw_rev |= step;

	DPRINTF(sc, "Detected crf-id 0x%x, cnv-id 0x%x\n",
	    sc->sc_hw_crf_id, sc->sc_hw_cnv_id);
}

/*
 * RX notification processing. Called from the DPC with osdep_lock held.
 */
void
iwx_notif_intr(struct iwx_softc *sc)
{
	uint16_t hw;

	IWX_DMA_SYNC(sc);

	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_AX210) {
		uint16_t *status = sc->rxq.stat_dma.vaddr;
		hw = le16toh(*status) & 0xfff;
	} else {
		struct iwx_rb_status *stat = sc->rxq.stat_dma.vaddr;
		hw = le16toh(stat->closed_rb_num) & 0xfff;
	}
	hw &= (IWX_RX_MQ_RING_COUNT - 1);
	while (sc->rxq.cur != hw) {
		struct iwx_rx_data *data = &sc->rxq.data[sc->rxq.cur];

		/* D-TR8: diagnostics only, iwx trusts index order */
		if (sc->sc_device_family == IWX_DEVICE_FAMILY_AX210) {
			const struct iwx_rx_completion_desc *cd =
			    sc->rxq.used_desc_dma.vaddr;
			if (le16toh(cd[sc->rxq.cur].rbid) != sc->rxq.cur)
				sc->rxq.rbid_mismatch++;
		}
		iwx_rx_pkt(sc, data);
		sc->rxq.cur = (sc->rxq.cur + 1) % IWX_RX_MQ_RING_COUNT;
	}

	/*
	 * Tell the firmware what we have processed.
	 * Seems like the hardware gets upset unless we align the write by 8??
	 */
	hw = (hw == 0) ? IWX_RX_MQ_RING_COUNT - 1 : hw - 1;
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		IWX_WRITE(sc, IWX_HBUS_TARG_WRPTR,
		    (hw & ~7) | IWX_HBUS_TARG_WRPTR_RX_Q(0));
	} else
		IWX_WRITE(sc, IWX_RFH_Q0_FRBDCB_WIDX_TRG, hw & ~7);
}

/* ---- interrupts (D-TR3) ------------------------------------------------- */

static void
iwx_alive_intr(struct iwx_softc *sc)
{
	int i;

	/* Firmware has now configured the RFH. */
	for (i = 0; i < IWX_RX_MQ_RING_COUNT; i++)
		iwx_update_rx_desc(sc, &sc->rxq, i);
	if (sc->sc_device_family >= IWX_DEVICE_FAMILY_BZ) {
		IWX_WRITE(sc, IWX_HBUS_TARG_WRPTR,
		    8 | IWX_HBUS_TARG_WRPTR_RX_Q(0));
	} else
		IWX_WRITE(sc, IWX_RFH_Q0_FRBDCB_WIDX_TRG, 8);
}

static void
iwx_fatal(struct iwx_softc *sc, enum osdep_fatal_reason why)
{
	if (why == OSDEP_FATAL_FW_ERROR)
		sc->sc_flags |= IWX_FLAG_FW_ERR;
	if (why == OSDEP_FATAL_HW_ERROR || why == OSDEP_FATAL_CT_KILL)
		sc->sc_flags |= IWX_FLAG_HW_ERR;
	/* D-CMD1: wake every waiter so it can fail fast */
	if (why != OSDEP_FATAL_RFKILL) {
		osdep_event_signal(sc->sc_os, OSDEP_EV_ALIVE);
		osdep_event_signal(sc->sc_os, OSDEP_EV_INIT);
		osdep_event_signal(sc->sc_os, OSDEP_EV_CMD);
	}
	if ((sc->sc_flags & IWX_FLAG_SHUTDOWN) == 0)
		osdep_fatal(sc->sc_os, why);
}

/* ISR half of iwx_intr_msix(): DIRQL, MMIO only. */
void
iwx_isr_msix(struct iwx_softc *sc, uint32_t *inta_fh, uint32_t *inta_hw)
{
	uint32_t fh, hw;

	fh = IWX_READ(sc, IWX_CSR_MSIX_FH_INT_CAUSES_AD);
	hw = IWX_READ(sc, IWX_CSR_MSIX_HW_INT_CAUSES_AD);
	if (fh == 0xffffffff && hw == 0xffffffff) {
		/* device gone (surprise removal / D3cold) */
		*inta_fh = *inta_hw = 0;
		return;
	}
	IWX_WRITE(sc, IWX_CSR_MSIX_FH_INT_CAUSES_AD, fh);
	IWX_WRITE(sc, IWX_CSR_MSIX_HW_INT_CAUSES_AD, hw);
	*inta_fh = fh;
	*inta_hw = hw;
}

/* DPC half of iwx_intr_msix(). */
void
iwx_dpc_msix(struct iwx_softc *sc, uint32_t inta_fh, uint32_t inta_hw)
{
	int vector = 0;

	inta_fh &= sc->sc_fh_mask;
	inta_hw &= sc->sc_hw_mask;

	if (inta_fh & IWX_MSIX_FH_INT_CAUSES_Q0 ||
	    inta_fh & IWX_MSIX_FH_INT_CAUSES_Q1) {
		osdep_lock(sc->sc_os);
		iwx_notif_intr(sc);
		osdep_unlock(sc->sc_os);
	}

	/* firmware chunk loaded: only used by pre-gen2 loading, ignored */

	if (inta_hw & IWX_MSIX_HW_INT_CAUSES_REG_TOP_FATAL_ERR) {
		IWX_ERR(sc, "fatal hardware error\n");
		iwx_fatal(sc, OSDEP_FATAL_HW_ERROR);
		return;
	}

	if ((inta_fh & IWX_MSIX_FH_INT_CAUSES_FH_ERR) ||
	    (inta_hw & IWX_MSIX_HW_INT_CAUSES_REG_SW_ERR) ||
	    (inta_hw & IWX_MSIX_HW_INT_CAUSES_REG_SW_ERR_V2)) {
		IWX_ERR(sc, "fatal firmware error\n");
		iwx_fatal(sc, OSDEP_FATAL_FW_ERROR);
		return;
	}

	if (inta_hw & IWX_MSIX_HW_INT_CAUSES_REG_RF_KILL) {
		iwx_check_rfkill(sc);
		iwx_fatal(sc, OSDEP_FATAL_RFKILL);
	}

	if (inta_hw & IWX_MSIX_HW_INT_CAUSES_REG_HW_ERR) {
		IWX_ERR(sc, "hardware error, stopping device\n");
		iwx_fatal(sc, OSDEP_FATAL_HW_ERROR);
		return;
	}

	if (inta_hw & IWX_MSIX_HW_INT_CAUSES_REG_ALIVE)
		iwx_alive_intr(sc);

	/*
	 * Before sending the interrupt the HW disables it to prevent
	 * a nested interrupt. This is done by writing 1 to the corresponding
	 * bit in the mask register. After handling the interrupt, it should be
	 * re-enabled by clearing this bit. This register is defined as
	 * write 1 clear (W1C) register, meaning that it's being clear
	 * by writing 1 to the bit.
	 */
	IWX_WRITE(sc, IWX_CSR_MSIX_AUTOMASK_ST_AD, 1 << vector);
}

/* ISR half of iwx_intr(): returns 0 if the interrupt is not ours. */
int
iwx_isr_legacy(struct iwx_softc *sc)
{
	uint32_t r1;

	if (sc->sc_flags & IWX_FLAG_USE_ICT) {
		const uint32_t *ict = sc->ict_dma.vaddr;
		if (ict[sc->ict_cur] == 0 && IWX_READ(sc, IWX_CSR_INT) == 0)
			return 0;
	} else {
		r1 = IWX_READ(sc, IWX_CSR_INT);
		if (r1 == 0 || r1 == 0xffffffff ||
		    (r1 & 0xfffffff0) == 0xa5a5a5a0)
			return 0;
	}
	IWX_WRITE(sc, IWX_CSR_INT_MASK, 0);
	return 1;
}

/* DPC half of iwx_intr(). */
void
iwx_dpc_legacy(struct iwx_softc *sc)
{
	uint32_t r1, r2 = 0;

	IWX_WRITE(sc, IWX_CSR_INT_MASK, 0);

	if (sc->sc_flags & IWX_FLAG_USE_ICT) {
		uint32_t *ict = sc->ict_dma.vaddr;
		uint32_t tmp;

		IWX_DMA_SYNC(sc);
		tmp = htole32(ict[sc->ict_cur]);
		if (!tmp)
			goto out_ena;

		/*
		 * ok, there was something.  keep plowing until we have all.
		 */
		r1 = r2 = 0;
		while (tmp) {
			r1 |= tmp;
			ict[sc->ict_cur] = 0;
			sc->ict_cur = (sc->ict_cur+1) % IWX_ICT_COUNT;
			tmp = htole32(ict[sc->ict_cur]);
		}

		/* this is where the fun begins.  don't ask */
		if (r1 == 0xffffffff)
			r1 = 0;

		/* i am not expected to understand this */
		if (r1 & 0xc0000)
			r1 |= 0x8000;
		r1 = (0xff & r1) | ((0xff00 & r1) << 16);
	} else {
		r1 = IWX_READ(sc, IWX_CSR_INT);
		if (r1 == 0xffffffff || (r1 & 0xfffffff0) == 0xa5a5a5a0)
			return;
		r2 = IWX_READ(sc, IWX_CSR_FH_INT_STATUS);
	}
	if (r1 == 0 && r2 == 0) {
		goto out_ena;
	}

	IWX_WRITE(sc, IWX_CSR_INT, r1 | ~(uint32_t)sc->sc_intmask);

	if (r1 & IWX_CSR_INT_BIT_ALIVE) {
		int i;

		/* Firmware has now configured the RFH. */
		for (i = 0; i < IWX_RX_MQ_RING_COUNT; i++)
			iwx_update_rx_desc(sc, &sc->rxq, i);
		IWX_WRITE(sc, IWX_RFH_Q0_FRBDCB_WIDX_TRG, 8);
	}

	if (r1 & IWX_CSR_INT_BIT_RF_KILL) {
		iwx_check_rfkill(sc);
		iwx_fatal(sc, OSDEP_FATAL_RFKILL);
		goto out_ena;
	}

	if (r1 & IWX_CSR_INT_BIT_SW_ERR) {
		IWX_ERR(sc, "fatal firmware error\n");
		iwx_fatal(sc, OSDEP_FATAL_FW_ERROR);
		return;
	}

	if (r1 & IWX_CSR_INT_BIT_HW_ERR) {
		IWX_ERR(sc, "hardware error, stopping device\n");
		iwx_fatal(sc, OSDEP_FATAL_HW_ERROR);
		return;
	}

	/* firmware chunk loaded */
	if (r1 & IWX_CSR_INT_BIT_FH_TX) {
		IWX_WRITE(sc, IWX_CSR_FH_INT_STATUS, IWX_CSR_FH_INT_TX_MASK);
	}

	if (r1 & (IWX_CSR_INT_BIT_FH_RX | IWX_CSR_INT_BIT_SW_RX |
	    IWX_CSR_INT_BIT_RX_PERIODIC)) {
		if (r1 & (IWX_CSR_INT_BIT_FH_RX | IWX_CSR_INT_BIT_SW_RX)) {
			IWX_WRITE(sc, IWX_CSR_FH_INT_STATUS, IWX_CSR_FH_INT_RX_MASK);
		}
		if (r1 & IWX_CSR_INT_BIT_RX_PERIODIC) {
			IWX_WRITE(sc, IWX_CSR_INT, IWX_CSR_INT_BIT_RX_PERIODIC);
		}

		/* Disable periodic interrupt; we use it as just a one-shot. */
		IWX_WRITE_1(sc, IWX_CSR_INT_PERIODIC_REG, IWX_CSR_INT_PERIODIC_DIS);

		/*
		 * Enable periodic interrupt in 8 msec only if we received
		 * real RX interrupt (instead of just periodic int), to catch
		 * any dangling Rx interrupt.  If it was just the periodic
		 * interrupt, there was no dangling Rx activity, and no need
		 * to extend the periodic interrupt; one-shot is enough.
		 */
		if (r1 & (IWX_CSR_INT_BIT_FH_RX | IWX_CSR_INT_BIT_SW_RX))
			IWX_WRITE_1(sc, IWX_CSR_INT_PERIODIC_REG,
			    IWX_CSR_INT_PERIODIC_ENA);

		osdep_lock(sc->sc_os);
		iwx_notif_intr(sc);
		osdep_unlock(sc->sc_os);
	}

 out_ena:
	iwx_restore_interrupts(sc);
}
