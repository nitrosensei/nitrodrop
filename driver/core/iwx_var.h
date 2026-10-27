/*	$OpenBSD: if_iwxvar.h,v 1.50 2026/03/20 08:38:21 stsp Exp $	*/

/*
 * Copyright (c) 2014 genua mbh <info@genua.de>
 * Copyright (c) 2014 Fixup Software Ltd.
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
 * airdrop-win port notes (driver/core/iwx_var.h)
 *
 * Derived from OpenBSD src/sys/dev/pci/if_iwxvar.h rev 1.50 (2026/03/20).
 * Everything tied to net80211, mbufs, bus_dma maps, tasks, BlockAck
 * reordering, keys, scanning and radiotap was removed. struct iwx_softc
 * keeps the iwx field names so the ported code in iwx_*.c reads like
 * if_iwx.c. Additions are marked "airdrop:".
 */
#ifndef AIRDROP_IWX_VAR_H
#define AIRDROP_IWX_VAR_H

#include "iwx_reg.h"

#define IWX_UCODE_SECT_MAX 69

#define IWX_FW_STATUS_NONE		0
#define IWX_FW_STATUS_INPROGRESS	1
#define IWX_FW_STATUS_DONE		2

enum iwx_ucode_type {
	IWX_UCODE_TYPE_REGULAR,
	IWX_UCODE_TYPE_INIT,
	IWX_UCODE_TYPE_WOW,
	IWX_UCODE_TYPE_REGULAR_USNIFFER,
	IWX_UCODE_TYPE_MAX
};

struct iwx_fw_onesect {
	const void *fws_data;	/* points into fw_rawdata */
	uint32_t fws_len;
	uint32_t fws_devoff;
};

struct iwx_fw_sects {
	struct iwx_fw_onesect fw_sect[IWX_UCODE_SECT_MAX];
	size_t fw_totlen;
	int fw_count;
};

struct iwx_fw_info {
	uint8_t *fw_rawdata;	/* airdrop: owned by osdep_fw_load() */
	size_t fw_rawsize;
	int fw_status;

	struct iwx_fw_sects fw_sects[IWX_UCODE_TYPE_MAX];

	/* FW debug data parsed for driver usage */
	int dbg_dest_tlv_init;
	const uint8_t *dbg_dest_ver;
	uint8_t n_dest_reg;
	const struct iwx_fw_dbg_dest_tlv_v1 *dbg_dest_tlv_v1;
	const struct iwx_fw_dbg_conf_tlv *dbg_conf_tlv[IWX_FW_DBG_CONF_MAX];
	size_t dbg_conf_tlv_len[IWX_FW_DBG_CONF_MAX];

	/* Copy of firmware image loader found in file. */
	uint8_t *iml;
	size_t iml_len;

	/* Copy of PNVM image found in file (none for So+HR). */
	uint8_t *pnvm;
	size_t pnvm_len;
};

/* airdrop: channel list reported to user mode (from NVM / MCC) */
#define IWX_NUM_2GHZ_CHANNELS	14
#define IWX_NUM_5GHZ_CHANNELS	37
#define IWX_MAX_CHANNELS	(IWX_NUM_2GHZ_CHANNELS + IWX_NUM_5GHZ_CHANNELS)

struct iwx_chan {
	uint8_t		ieee;		/* channel number, 0 = invalid */
	uint32_t	nvm_flags;	/* IWX_NVM_CHANNEL_* as reported */
};

struct iwx_nvm_data {
	int n_hw_addrs;
	uint8_t hw_addr[ETHER_ADDR_LEN];

	int sku_cap_band_24GHz_enable;
	int sku_cap_band_52GHz_enable;
	int sku_cap_11n_enable;
	int sku_cap_11ac_enable;
	int sku_cap_11ax_enable;
	int sku_cap_amt_enable;
	int sku_cap_ipan_enable;
	int sku_cap_mimo_disable;
	int lar_enabled;

	uint8_t valid_tx_ant, valid_rx_ant;

	uint16_t nvm_version;

	/* airdrop: replaces net80211 ic_channels[] */
	struct iwx_chan channels[IWX_MAX_CHANNELS];
	int nchan;
};

#define IWX_MAX_CMD_TBS_PER_TFD 2

struct iwx_host_cmd {
	const void *data[IWX_MAX_CMD_TBS_PER_TFD];
	struct iwx_rx_packet *resp_pkt;
	size_t resp_pkt_len;
	uint32_t flags;
	uint16_t len[IWX_MAX_CMD_TBS_PER_TFD];
	uint32_t id;
};

#define IWX_TX_RING_COUNT	IWX_DEFAULT_QUEUE_SIZE

struct iwx_tx_data {
	uint64_t	cmd_paddr;	/* slot in ring->cmd_dma */
	int		flags;
#define IWX_TXDATA_FLAG_CMD_IS_NARROW	0x01
#define IWX_TXDATA_FLAG_BIG_CMD		0x02	/* airdrop: uses sc->cmd_big_dma */
	volatile int	done;		/* airdrop: completion flag for waiter */
	/* airdrop (M3): injected frame bookkeeping, data queues only */
	int		inflight;	/* waiting for TX_CMD response */
	uint32_t	cookie;		/* iwx_tx_params.cookie */
	uint32_t	txr_flags;	/* IWX_TXR_F_* decided at submit */
	uint16_t	seq_ctl;	/* sequence control as sent */
};

struct iwx_tx_ring {
	struct iwx_dma_info	desc_dma;
	struct iwx_dma_info	cmd_dma;
	struct iwx_dma_info	bc_tbl;
	struct iwx_tfh_tfd	*desc;
	struct iwx_device_cmd	*cmd;
	struct iwx_tx_data	data[IWX_TX_RING_COUNT];
	int			qid;
	int			queued;
	int			cur;
	int			cur_hw;
	int			tail;
	int			tail_hw;
	int			tid;
	int			allocated;	/* airdrop: lazily allocated */
	/* airdrop (M3): payload buffers, IWX_TX_MAX_INFLIGHT slots */
	struct iwx_dma_info	pay_dma;
};

#define IWX_RX_MQ_RING_COUNT	512
#define IWX_RBUF_SIZE		4096

struct iwx_rx_data {
	struct iwx_dma_info	buf;	/* airdrop: fixed 4 KiB RB, never swapped */
};

struct iwx_rx_ring {
	struct iwx_dma_info	free_desc_dma;
	struct iwx_dma_info	stat_dma;
	struct iwx_dma_info	used_desc_dma;
	void			*desc;
	struct iwx_rx_data	data[IWX_RX_MQ_RING_COUNT];
	int			cur;
	uint32_t		rbid_mismatch;	/* airdrop: diagnostics */
};

#define IWX_FLAG_USE_ICT	0x01
#define IWX_FLAG_RFKILL		0x02
#define IWX_FLAG_MAC_ACTIVE	0x08
#define IWX_FLAG_BINDING_ACTIVE	0x10
#define IWX_FLAG_STA_ACTIVE	0x20
#define IWX_FLAG_TE_ACTIVE	0x40
#define IWX_FLAG_HW_ERR		0x80
#define IWX_FLAG_SHUTDOWN	0x100
#define IWX_FLAG_PHY_ACTIVE	0x800
#define IWX_FLAG_FW_ERR		0x1000	/* airdrop: firmware asserted */

struct iwx_ucode_status {
	uint32_t uc_lmac_error_event_table[2];
	uint32_t uc_umac_error_event_table;
	uint32_t uc_log_event_table;
	unsigned int error_event_table_tlv_status;

	volatile int uc_ok;
	volatile int uc_intr;
};

#define IWX_ERROR_EVENT_TABLE_LMAC1	(1 << 0)
#define IWX_ERROR_EVENT_TABLE_LMAC2	(1 << 1)
#define IWX_ERROR_EVENT_TABLE_UMAC	(1 << 2)

#define IWX_CMD_RESP_MAX IWX_PAGE_SIZE

enum IWX_CMD_MODE {
	IWX_CMD_ASYNC		= (1 << 0),
	IWX_CMD_WANT_RESP	= (1 << 1),
	IWX_CMD_SEND_IN_RFKILL	= (1 << 2),
};

struct iwx_device_cfg {
	const char *fw_name;
	const char *pnvm_name;
	int	    tx_with_siso_diversity;
	int	    uhb_supported;
	int	    xtal_latency;
	int	    low_latency_xtal;
};

/*
 * Linux file names (linux-firmware intel/iwlwifi/). OpenBSD fw_update(8)
 * ships the same blobs renamed "iwx-so-a0-hr-b0-77".
 */
#define IWX_SO_A_HR_B_FW	"iwlwifi-so-a0-hr-b0-77.ucode"


/* Device configuration matching (if_iwxvar.h). */
#define IWX_CFG_ANY (~0)

#define IWX_CFG_MAC_TYPE_QU		0x33
#define IWX_CFG_MAC_TYPE_QUZ		0x35
#define IWX_CFG_MAC_TYPE_QNJ		0x36
#define IWX_CFG_MAC_TYPE_SO		0x37
#define IWX_CFG_MAC_TYPE_SNJ		0x42
#define IWX_CFG_MAC_TYPE_SOF		0x43
#define IWX_CFG_MAC_TYPE_MA		0x44
#define IWX_CFG_MAC_TYPE_BZ		0x46
#define IWX_CFG_MAC_TYPE_GL		0x47
#define IWX_CFG_MAC_TYPE_BZ_W		0x4B

#define IWX_CFG_RF_TYPE_JF2		0x105
#define IWX_CFG_RF_TYPE_JF1		0x108
#define IWX_CFG_RF_TYPE_HR2		0x10a
#define IWX_CFG_RF_TYPE_HR1		0x10c
#define IWX_CFG_RF_TYPE_GF		0x10d
#define IWX_CFG_RF_TYPE_MR		0x110
#define IWX_CFG_RF_TYPE_MS		0x111
#define IWX_CFG_RF_TYPE_FM		0x112

#define IWX_CFG_NO_160			0x1
#define IWX_CFG_160			0x0

#define IWX_CFG_NO_CDB			0x0
#define IWX_CFG_CDB			0x1

#define IWX_SUBDEVICE_RF_ID(subdevice)	((uint16_t)((subdevice) & 0x00f0) >> 4)
#define IWX_SUBDEVICE_NO_160(subdevice)	((uint16_t)((subdevice) & 0x0200) >> 9)
#define IWX_SUBDEVICE_CORES(subdevice)	((uint16_t)((subdevice) & 0x1c00) >> 10)

struct iwx_dev_info {
	uint16_t device;
	uint16_t subdevice;
	uint16_t mac_type;
	uint16_t rf_type;
	uint8_t mac_step;
	uint8_t rf_id;
	uint8_t no_160;
	uint8_t cores;
	uint8_t cdb;
	uint8_t jacket;
	const struct iwx_device_cfg *cfg;
};

struct iwx_phy_ctxt {
	uint16_t id;
	uint16_t color;
	uint32_t ref;
	uint8_t channel;	/* airdrop: IEEE channel number */
	uint8_t sco;
	uint8_t vht_chan_width;
};

struct iwx_self_init_dram {
	struct iwx_dma_info *fw;
	int lmac_cnt;
	int umac_cnt;
	struct iwx_dma_info *paging;
	int paging_cnt;
};

#define IWX_STATION_ID 0
#define IWX_AUX_STA_ID 1
#define IWX_MONITOR_STA_ID 2

#define IWX_ICT_SIZE		4096
#define IWX_ICT_COUNT		(IWX_ICT_SIZE / sizeof (uint32_t))
#define IWX_ICT_PADDR_SHIFT	12

#define IWX_MAX_FW_CMD_VERSIONS	704

/* airdrop: radio state machine exposed through the IOCTL interface */
enum iwx_radio_state {
	IWX_STATE_DOWN = 0,	/* device stopped (after preinit) */
	IWX_STATE_FW_READY,	/* firmware running, init_hw done (M1) */
	IWX_STATE_RADIO_ON,	/* MAC/PHY/binding up on fixed channel (M2/M3) */
	IWX_STATE_ERROR		/* fatal error, needs stop + start */
};

struct iwx_softc {
	void *sc_os;		/* airdrop: backend context passed to osdep_* */
	int attached;

	uint16_t sc_pid;	/* PCI device ID */
	uint16_t sc_sdev;	/* airdrop: PCI subsystem device ID */
	int sc_msix;

	/* TX/RX rings. */
	struct iwx_tx_ring txq[IWX_NUM_TX_QUEUES];
	struct iwx_rx_ring rxq;
	int qfullmsk;
	int qenablemsk;
	int max_tfd_queue_size;

	/* ICT table. */
	struct iwx_dma_info	ict_dma;
	int			ict_cur;

	int sc_hw_rev;
#define IWX_SILICON_A_STEP	0
#define IWX_SILICON_B_STEP	1
#define IWX_SILICON_C_STEP	2
#define IWX_SILICON_Z_STEP	0xf
	int sc_hw_id;
	int sc_hw_crf_id;
	int sc_hw_cnv_id;
	int sc_hw_rf_id;
	int sc_device_family;
#define IWX_DEVICE_FAMILY_22000	1
#define IWX_DEVICE_FAMILY_AX210	2
#define IWX_DEVICE_FAMILY_BZ	3
	uint32_t sc_sku_id[3];
	uint32_t mac_addr_from_csr;

	struct iwx_dma_info ctxt_info_dma;
	struct iwx_self_init_dram init_dram;
	struct iwx_dma_info prph_scratch_dma;
	struct iwx_dma_info prph_info_dma;
	struct iwx_dma_info iml_dma;

	volatile int sc_init_complete;
#define IWX_INIT_COMPLETE	0x01
#define IWX_CALIB_COMPLETE	0x02
#define IWX_PNVM_COMPLETE	0x04

	struct iwx_ucode_status sc_uc;
	char sc_fwver[32];

	int sc_capaflags;
	int sc_capa_max_probe_len;
	int sc_capa_n_scan_channels;
	uint8_t sc_ucode_api[howmany(IWX_NUM_UCODE_TLV_API, NBBY)];
	uint8_t sc_enabled_capa[howmany(IWX_NUM_UCODE_TLV_CAPA, NBBY)];
	struct iwx_fw_cmd_version cmd_versions[IWX_MAX_FW_CMD_VERSIONS];
	int n_cmd_versions;
	int sc_rate_n_flags_version;
	int sc_use_mld_api;

	int sc_intmask;
	volatile int sc_flags;

	uint32_t sc_fh_init_mask;
	uint32_t sc_hw_init_mask;
	uint32_t sc_fh_mask;
	uint32_t sc_hw_mask;

	volatile int sc_generation;

	int sc_cap_off; /* PCIe capability offset in config space */

	const char *sc_fwname;
	struct iwx_fw_info sc_fw;
	int sc_fw_phy_config;
	struct iwx_tlv_calib_ctrl sc_default_calib[IWX_UCODE_TYPE_MAX];

	struct iwx_nvm_data sc_nvm;
	int sc_bf_enabled;
	const char *sc_pnvm_name;

	uint8_t *sc_cmd_resp_pkt[IWX_TX_RING_COUNT];
	size_t sc_cmd_resp_len[IWX_TX_RING_COUNT];
	int sc_nic_locks;

	/* airdrop: one DMA buffer for commands > IWX_DEF_CMD_PAYLOAD_SIZE */
	struct iwx_dma_info cmd_big_dma;
	int cmd_big_idx;		/* ring slot using it, -1 if free */

	struct iwx_phy_ctxt sc_phyctxt[IWX_NUM_PHY_CTX];

	int sc_pm_support;
	int sc_ltr_enabled;

	int sc_integrated;
	int sc_tx_with_siso_diversity;
	int sc_ltr_delay;
	int sc_xtal_latency;
	int sc_low_latency_xtal;
	int sc_uhb_supported;
	int sc_umac_prph_offset;
	int sc_imr_enabled;

	/* airdrop: */
	uint8_t sc_macaddr[ETHER_ADDR_LEN];	/* address in use (NVM) */
	volatile int sc_state;			/* enum iwx_radio_state */
	volatile int sc_rx_enabled;		/* deliver MPDUs upward */
	uint32_t sc_last_fw_error;		/* error_id from error log */
	uint32_t sc_stat_rx_frames;
	uint32_t sc_stat_rx_dropped;
	uint32_t sc_stat_unhandled_notif;
	/* airdrop: radio (M2/M3), iwx_radio.c */
	int sc_radio_mode;			/* IWX_RADIO_MODE_* */
	uint32_t sc_radio_flags;		/* IWX_RADIO_F_* in use */
	uint8_t sc_bssid[ETHER_ADDR_LEN];
	uint16_t sc_beacon_int;
	int sc_tx_qid;				/* inject queue, 0 = none */
	int sc_tx_sta_id;			/* station owning sc_tx_qid */
	int sc_mcast_sta_active;		/* M3 multicast station added */
	int sc_bcast_sta_active;		/* M3 broadcast station added */
	int sc_rx_fcs_included;			/* IN_CRC32 filter set */
	int sc_rx_keep_bad_fcs;
	int sc_ampdu_toggle;
	uint32_t sc_ampdu_ref;
	uint16_t sc_tx_seq;			/* IWX_TX_F_ASSIGN_SEQ counter */
	uint32_t sc_stat_rx_bad_fcs;
	uint32_t sc_stat_tx_submitted;
	uint32_t sc_stat_tx_success;
	uint32_t sc_stat_tx_failed;
	uint32_t sc_stat_tx_flushed;
};

/* Register access through the OS abstraction (replaces bus_space). */
#define IWX_READ(sc, reg)		osdep_read32((sc)->sc_os, (uint32_t)(reg))
#define IWX_WRITE(sc, reg, val)		osdep_write32((sc)->sc_os, (uint32_t)(reg), (uint32_t)(val))
#define IWX_WRITE_1(sc, reg, val)	osdep_write8((sc)->sc_os, (uint32_t)(reg), (uint8_t)(val))
#define IWX_SETBITS(sc, reg, mask)	IWX_WRITE(sc, reg, IWX_READ(sc, reg) | (mask))
#define IWX_CLRBITS(sc, reg, mask)	IWX_WRITE(sc, reg, IWX_READ(sc, reg) & ~(mask))
#define IWX_BARRIER_WRITE(sc)		osdep_barrier((sc)->sc_os)
#define IWX_BARRIER_READ_WRITE(sc)	osdep_barrier((sc)->sc_os)
#define DELAY(us)			osdep_delay_us((uint32_t)(us))

#define DPRINTF(sc, ...)	osdep_log((sc)->sc_os, OSDEP_LOG_DEBUG, __VA_ARGS__)
#define IWX_LOG(sc, ...)	osdep_log((sc)->sc_os, OSDEP_LOG_INFO, __VA_ARGS__)
#define IWX_ERR(sc, ...)	osdep_log((sc)->sc_os, OSDEP_LOG_ERR, __VA_ARGS__)

/* ---- iwx_fw.c ----------------------------------------------------------- */
uint8_t	iwx_lookup_cmd_ver(struct iwx_softc *, uint8_t, uint8_t);
uint8_t	iwx_lookup_notif_ver(struct iwx_softc *, uint8_t, uint8_t);
void	iwx_fw_version_str(char *, size_t, uint32_t, uint32_t, uint32_t);
int	iwx_parse_firmware(struct iwx_softc *, uint8_t *, size_t);
int	iwx_read_firmware(struct iwx_softc *);
void	iwx_fw_info_free(struct iwx_softc *);
int	iwx_get_num_sections(const struct iwx_fw_sects *, int);

/* ---- iwx_trans.c ---------------------------------------------------------- */
uint32_t iwx_read_prph_unlocked(struct iwx_softc *, uint32_t);
uint32_t iwx_read_prph(struct iwx_softc *, uint32_t);
void	iwx_write_prph_unlocked(struct iwx_softc *, uint32_t, uint32_t);
void	iwx_write_prph(struct iwx_softc *, uint32_t, uint32_t);
uint32_t iwx_read_umac_prph_unlocked(struct iwx_softc *, uint32_t);
uint32_t iwx_read_umac_prph(struct iwx_softc *, uint32_t);
void	iwx_write_umac_prph_unlocked(struct iwx_softc *, uint32_t, uint32_t);
void	iwx_write_umac_prph(struct iwx_softc *, uint32_t, uint32_t);
int	iwx_read_mem(struct iwx_softc *, uint32_t, void *, int);
int	iwx_write_mem(struct iwx_softc *, uint32_t, const void *, int);
int	iwx_poll_bit(struct iwx_softc *, int, uint32_t, uint32_t, int);
int	iwx_nic_lock(struct iwx_softc *);
void	iwx_nic_assert_locked(struct iwx_softc *);
void	iwx_nic_unlock(struct iwx_softc *);
int	iwx_set_bits_mask_prph(struct iwx_softc *, uint32_t, uint32_t, uint32_t);
int	iwx_set_bits_prph(struct iwx_softc *, uint32_t, uint32_t);
int	iwx_clear_bits_prph(struct iwx_softc *, uint32_t, uint32_t);
int	iwx_dma_contig_alloc(struct iwx_softc *, struct iwx_dma_info *, size_t, size_t);
void	iwx_dma_contig_free(struct iwx_softc *, struct iwx_dma_info *);
int	iwx_alloc_rx_ring(struct iwx_softc *, struct iwx_rx_ring *);
void	iwx_disable_rx_dma(struct iwx_softc *);
void	iwx_reset_rx_ring(struct iwx_softc *, struct iwx_rx_ring *);
void	iwx_free_rx_ring(struct iwx_softc *, struct iwx_rx_ring *);
void	iwx_update_rx_desc(struct iwx_softc *, struct iwx_rx_ring *, int);
int	iwx_alloc_tx_ring(struct iwx_softc *, struct iwx_tx_ring *, int);
void	iwx_reset_tx_ring(struct iwx_softc *, struct iwx_tx_ring *);
void	iwx_free_tx_ring(struct iwx_softc *, struct iwx_tx_ring *);
void	iwx_enable_rfkill_int(struct iwx_softc *);
int	iwx_check_rfkill(struct iwx_softc *);
void	iwx_enable_interrupts(struct iwx_softc *);
void	iwx_enable_fwload_interrupt(struct iwx_softc *);
void	iwx_restore_interrupts(struct iwx_softc *);
void	iwx_disable_interrupts(struct iwx_softc *);
void	iwx_ict_reset(struct iwx_softc *);
int	iwx_set_hw_ready(struct iwx_softc *);
int	iwx_prepare_card_hw(struct iwx_softc *);
int	iwx_force_power_gating(struct iwx_softc *);
void	iwx_apm_config(struct iwx_softc *);
int	iwx_apm_init(struct iwx_softc *);
void	iwx_apm_stop(struct iwx_softc *);
void	iwx_init_msix_hw(struct iwx_softc *);
void	iwx_conf_msix_hw(struct iwx_softc *, int);
int	iwx_clear_persistence_bit(struct iwx_softc *);
void	iwx_sw_reset(struct iwx_softc *);
int	iwx_start_hw(struct iwx_softc *);
void	iwx_stop_device(struct iwx_softc *);
void	iwx_nic_config(struct iwx_softc *);
int	iwx_nic_rx_init(struct iwx_softc *);
int	iwx_nic_init(struct iwx_softc *);
void	iwx_set_ltr(struct iwx_softc *);
int	iwx_init_fw_sec(struct iwx_softc *, const struct iwx_fw_sects *,
	    struct iwx_context_info_dram *);
int	iwx_ctxt_info_gen3_init(struct iwx_softc *, const struct iwx_fw_sects *);
void	iwx_ctxt_info_free_fw_img(struct iwx_softc *);
void	iwx_ctxt_info_free_paging(struct iwx_softc *);
int	iwx_apply_debug_destination(struct iwx_softc *);
int	iwx_load_firmware(struct iwx_softc *);
int	iwx_start_fw(struct iwx_softc *);
void	iwx_get_crf_id(struct iwx_softc *);
void	iwx_notif_intr(struct iwx_softc *);

/* ---- iwx_cmd.c ------------------------------------------------------------ */
int	iwx_send_cmd(struct iwx_softc *, struct iwx_host_cmd *);
int	iwx_send_cmd_pdu(struct iwx_softc *, uint32_t, uint32_t, uint16_t,
	    const void *);
int	iwx_send_cmd_status(struct iwx_softc *, struct iwx_host_cmd *,
	    uint32_t *);
int	iwx_send_cmd_pdu_status(struct iwx_softc *, uint32_t, uint16_t,
	    const void *, uint32_t *);
void	iwx_free_resp(struct iwx_softc *, struct iwx_host_cmd *);
void	iwx_cmd_done(struct iwx_softc *, int, int, int);
int	iwx_rx_pkt_valid(const struct iwx_rx_packet *);
void	iwx_rx_pkt(struct iwx_softc *, struct iwx_rx_data *);
int	iwx_wait_cond(struct iwx_softc *, enum osdep_event_id,
	    const volatile int *, int, uint32_t);

/* ---- iwx_init.c ------------------------------------------------------------ */
const struct iwx_device_cfg *iwx_find_device_cfg(struct iwx_softc *);
void	iwx_post_alive(struct iwx_softc *);
int	iwx_load_pnvm(struct iwx_softc *);
int	iwx_load_ucode_wait_alive(struct iwx_softc *);
int	iwx_run_init_mvm_ucode(struct iwx_softc *, int);
int	iwx_nvm_get(struct iwx_softc *);
void	iwx_init_channel_map(struct iwx_softc *, const uint16_t *,
	    const uint32_t *, int);
int	iwx_is_valid_mac_addr(const uint8_t *);
uint8_t	iwx_fw_valid_tx_ant(struct iwx_softc *);
uint8_t	iwx_fw_valid_rx_ant(struct iwx_softc *);
int	iwx_init_hw(struct iwx_softc *);
void	iwx_nic_error(struct iwx_softc *);
void	iwx_nic_umac_error(struct iwx_softc *);
const char *iwx_desc_lookup(uint32_t);
int	iwx_send_update_mcc_cmd(struct iwx_softc *, const char *);
int	iwx_disable_beacon_filter(struct iwx_softc *);

/* ---- iwx_radio.c ------------------------------------------------------------ */
#define IWX_RADIO_MODE_OFF	0
#define IWX_RADIO_MODE_LISTENER	1	/* M2 */
#define IWX_RADIO_MODE_IBSS	2	/* M3 */

/* M3 station / queue layout (fixed, see iwx_radio.c D-RA notes) */
#define IWX_IBSS_BCAST_STA_ID	IWX_MONITOR_STA_ID	/* 2 */
#define IWX_IBSS_MCAST_STA_ID	3
#define IWX_IBSS_TX_QUEUE	IWX_DQA_INJECT_MONITOR_QUEUE	/* 1 */
#define IWX_IBSS_CAB_QUEUE	2
#define IWX_TX_PAYLOAD_SLOT	2368	/* >= IWX_TX_MAX_FRAME, 64-aligned */

void	iwx_rx_mpdu_mq(struct iwx_softc *, const void *, size_t);
void	iwx_rx_tx_cmd(struct iwx_softc *, const struct iwx_rx_packet *);
void	iwx_txq_flush_report(struct iwx_softc *, struct iwx_tx_ring *);
struct iwx_tx_params;
/* builders exported for the host tests (byte-exact payload checks) */
void	iwx_radio_fill_mac_cmd(struct iwx_softc *, struct iwx_mac_ctx_cmd *,
	    uint32_t, int);
void	iwx_radio_fill_add_sta(struct iwx_softc *, struct iwx_add_sta_cmd *,
	    int, int, const uint8_t *, int);
void	iwx_radio_fill_sf(struct iwx_sf_cfg_cmd *, uint32_t, int);
uint32_t iwx_radio_build_beacon(struct iwx_softc *, uint8_t *, size_t);
int	iwx_tx_rate_n_flags(struct iwx_softc *, const struct iwx_tx_params *,
	    uint32_t *);

/* ---- public API used by the OS backend (iwx_core.h) ------------------------- */
#include "iwx_core.h"

#endif /* AIRDROP_IWX_VAR_H */
