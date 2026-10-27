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
 * airdrop-win port notes (driver/core/iwx_fw.c)
 *
 * Firmware file (.ucode TLV) parsing, ported from OpenBSD if_iwx.c
 * rev 1.230: iwx_lookup_cmd_ver(), iwx_lookup_notif_ver(),
 * iwx_fw_version_str(), iwx_store_cscheme(), iwx_firmware_store_section(),
 * iwx_set_default_calib(), iwx_fw_info_free(), iwx_read_firmware(),
 * iwx_get_num_sections().
 *
 * Divergences from iwx (all intentional):
 *   D-FW1 iwx_read_firmware() is split: iwx_parse_firmware() works on a
 *         memory buffer so the host tests can feed it real/synthetic blobs.
 *   D-FW2 extra bounds checks iwx lacks: file shorter than the TLV header,
 *         FW_DBG_DEST / FW_DBG_CONF TLVs shorter than their fixed part.
 *         A hostile firmware file must not make the kernel read out of
 *         bounds (the file sits in the driver store, but defence in depth).
 *   D-FW3 snprintf is not available to the core in kernel mode; the
 *         version string is formatted by a local helper, same output.
 *   D-FW4 the parsed debug-destination TLV is stored but never applied:
 *         iwx applies it only on the pre-AX210 context-info path.
 */

#include "iwx_var.h"

uint8_t
iwx_lookup_cmd_ver(struct iwx_softc *sc, uint8_t grp, uint8_t cmd)
{
	const struct iwx_fw_cmd_version *entry;
	int i;

	for (i = 0; i < sc->n_cmd_versions; i++) {
		entry = &sc->cmd_versions[i];
		if (entry->group == grp && entry->cmd == cmd)
			return entry->cmd_ver;
	}

	return IWX_FW_CMD_VER_UNKNOWN;
}

uint8_t
iwx_lookup_notif_ver(struct iwx_softc *sc, uint8_t grp, uint8_t cmd)
{
	const struct iwx_fw_cmd_version *entry;
	int i;

	for (i = 0; i < sc->n_cmd_versions; i++) {
		entry = &sc->cmd_versions[i];
		if (entry->group == grp && entry->cmd == cmd)
			return entry->notif_ver;
	}

	return IWX_FW_CMD_VER_UNKNOWN;
}

/* D-FW3: minimal formatting helpers (no snprintf in the kernel core). */
static size_t
fmt_put(char *buf, size_t bufsize, size_t pos, char c)
{
	if (pos + 1 < bufsize)
		buf[pos] = c;
	return pos + 1;
}

static size_t
fmt_dec(char *buf, size_t bufsize, size_t pos, uint32_t v)
{
	char tmp[10];
	int n = 0;

	do {
		tmp[n++] = (char)('0' + (v % 10));
		v /= 10;
	} while (v != 0);
	while (n > 0)
		pos = fmt_put(buf, bufsize, pos, tmp[--n]);
	return pos;
}

static size_t
fmt_hex8(char *buf, size_t bufsize, size_t pos, uint32_t v)
{
	static const char hex[] = "0123456789abcdef";
	int i;

	for (i = 7; i >= 0; i--)
		pos = fmt_put(buf, bufsize, pos, hex[(v >> (i * 4)) & 0xf]);
	return pos;
}

void
iwx_fw_version_str(char *buf, size_t bufsize,
    uint32_t major, uint32_t minor, uint32_t api)
{
	size_t pos = 0;

	if (bufsize == 0)
		return;
	/*
	 * Starting with major version 35 the Linux driver prints the minor
	 * version in hexadecimal.
	 */
	pos = fmt_dec(buf, bufsize, pos, major);
	pos = fmt_put(buf, bufsize, pos, '.');
	if (major >= 35)
		pos = fmt_hex8(buf, bufsize, pos, minor);
	else
		pos = fmt_dec(buf, bufsize, pos, minor);
	pos = fmt_put(buf, bufsize, pos, '.');
	pos = fmt_dec(buf, bufsize, pos, api);
	buf[pos < bufsize ? pos : bufsize - 1] = '\0';
}

static int
iwx_store_cscheme(struct iwx_softc *sc, const uint8_t *data, size_t dlen)
{
	const struct iwx_fw_cscheme_list *l = (const void *)data;

	(void)sc;
	if (dlen < sizeof(*l) ||
	    dlen < sizeof(l->size) + l->size * sizeof(*l->cs))
		return EINVAL;

	/* we don't actually store anything for now, always use s/w crypto */

	return 0;
}

static int
iwx_firmware_store_section(struct iwx_softc *sc, enum iwx_ucode_type type,
    const uint8_t *data, size_t dlen)
{
	struct iwx_fw_sects *fws;
	struct iwx_fw_onesect *fwone;

	if (type >= IWX_UCODE_TYPE_MAX)
		return EINVAL;
	if (dlen < sizeof(uint32_t))
		return EINVAL;

	fws = &sc->sc_fw.fw_sects[type];
	if (fws->fw_count >= IWX_UCODE_SECT_MAX)
		return EINVAL;

	fwone = &fws->fw_sect[fws->fw_count];

	/* first 32bit are device load offset */
	memcpy(&fwone->fws_devoff, data, sizeof(uint32_t));

	/* rest is data */
	fwone->fws_data = data + sizeof(uint32_t);
	fwone->fws_len = (uint32_t)(dlen - sizeof(uint32_t));

	fws->fw_count++;
	fws->fw_totlen += fwone->fws_len;

	return 0;
}

#define IWX_DEFAULT_SCAN_CHANNELS	40
/* Newer firmware might support more channels. Raise this value if needed. */
#define IWX_MAX_SCAN_CHANNELS		67 /* as of iwx-cc-a0-62 firmware */

#pragma pack(push, 1)
struct iwx_tlv_calib_data {
	uint32_t ucode_type;
	struct iwx_tlv_calib_ctrl calib;
};
#pragma pack(pop)

static int
iwx_set_default_calib(struct iwx_softc *sc, const void *data)
{
	const struct iwx_tlv_calib_data *def_calib = data;
	uint32_t ucode_type = le32toh(def_calib->ucode_type);

	if (ucode_type >= IWX_UCODE_TYPE_MAX)
		return EINVAL;

	sc->sc_default_calib[ucode_type].flow_trigger =
	    def_calib->calib.flow_trigger;
	sc->sc_default_calib[ucode_type].event_trigger =
	    def_calib->calib.event_trigger;

	return 0;
}

void
iwx_fw_info_free(struct iwx_softc *sc)
{
	struct iwx_fw_info *fw = &sc->sc_fw;

	if (fw->fw_rawdata != NULL)
		osdep_fw_release(sc->sc_os, fw->fw_rawdata, fw->fw_rawsize);
	fw->fw_rawdata = NULL;
	fw->fw_rawsize = 0;
	/* don't touch fw->fw_status */
	memset(fw->fw_sects, 0, sizeof(fw->fw_sects));
	osdep_free(fw->iml, fw->iml_len);
	fw->iml = NULL;
	fw->iml_len = 0;
	osdep_free(fw->pnvm, fw->pnvm_len);
	fw->pnvm = NULL;
	fw->pnvm_len = 0;
	fw->dbg_dest_tlv_init = 0;
	fw->dbg_dest_ver = NULL;
	fw->dbg_dest_tlv_v1 = NULL;
	fw->n_dest_reg = 0;
	memset((void *)fw->dbg_conf_tlv, 0, sizeof(fw->dbg_conf_tlv));
	memset(fw->dbg_conf_tlv_len, 0, sizeof(fw->dbg_conf_tlv_len));
}

#define IWX_FW_ADDR_CACHE_CONTROL 0xC0000000

/*
 * Parse a .ucode image. 'data' must stay valid (and is owned by sc->sc_fw
 * on success) because section descriptors point into it.
 */
int
iwx_parse_firmware(struct iwx_softc *sc, uint8_t *rawdata, size_t rawsize)
{
	struct iwx_fw_info *fw = &sc->sc_fw;
	const struct iwx_tlv_ucode_header *uhdr;
	struct iwx_ucode_tlv tlv;
	uint32_t tlv_type = 0;
	const uint8_t *data;
	int err = 0;
	size_t len;

	fw->fw_rawdata = rawdata;
	fw->fw_rawsize = rawsize;

	sc->sc_capaflags = 0;
	sc->sc_capa_n_scan_channels = IWX_DEFAULT_SCAN_CHANNELS;
	memset(sc->sc_enabled_capa, 0, sizeof(sc->sc_enabled_capa));
	memset(sc->sc_ucode_api, 0, sizeof(sc->sc_ucode_api));
	memset(fw->fw_sects, 0, sizeof(fw->fw_sects));
	sc->n_cmd_versions = 0;

	/* D-FW2: iwx does not check the header length. */
	if (rawsize < sizeof(*uhdr)) {
		IWX_ERR(sc, "firmware file too short (%u bytes)\n",
		    (unsigned)rawsize);
		return EINVAL;
	}

	uhdr = (const void *)fw->fw_rawdata;
	if (*(const uint32_t *)fw->fw_rawdata != 0
	    || le32toh(uhdr->magic) != IWX_TLV_UCODE_MAGIC) {
		IWX_ERR(sc, "invalid firmware %s\n",
		    sc->sc_fwname ? sc->sc_fwname : "(buffer)");
		return EINVAL;
	}

	iwx_fw_version_str(sc->sc_fwver, sizeof(sc->sc_fwver),
	    IWX_UCODE_MAJOR(le32toh(uhdr->ver)),
	    IWX_UCODE_MINOR(le32toh(uhdr->ver)),
	    IWX_UCODE_API(le32toh(uhdr->ver)));

	data = uhdr->data;
	len = fw->fw_rawsize - sizeof(*uhdr);

	while (len >= sizeof(tlv)) {
		size_t tlv_len;
		const uint8_t *tlv_data;

		memcpy(&tlv, data, sizeof(tlv));
		tlv_len = le32toh(tlv.length);
		tlv_type = le32toh(tlv.type);

		len -= sizeof(tlv);
		data += sizeof(tlv);
		tlv_data = data;

		if (len < tlv_len) {
			IWX_ERR(sc, "firmware too short: %u bytes\n",
			    (unsigned)len);
			err = EINVAL;
			goto parse_out;
		}

		switch (tlv_type) {
		case IWX_UCODE_TLV_PROBE_MAX_LEN:
			if (tlv_len < sizeof(uint32_t)) {
				err = EINVAL;
				goto parse_out;
			}
			sc->sc_capa_max_probe_len
			    = (int)le32toh(*(const uint32_t *)tlv_data);
			if (sc->sc_capa_max_probe_len >
			    IWX_SCAN_OFFLOAD_PROBE_REQ_SIZE) {
				err = EINVAL;
				goto parse_out;
			}
			break;
		case IWX_UCODE_TLV_PAN:
			if (tlv_len) {
				err = EINVAL;
				goto parse_out;
			}
			sc->sc_capaflags |= IWX_UCODE_TLV_FLAGS_PAN;
			break;
		case IWX_UCODE_TLV_FLAGS:
			if (tlv_len < sizeof(uint32_t)) {
				err = EINVAL;
				goto parse_out;
			}
			/*
			 * Apparently there can be many flags, but Linux driver
			 * parses only the first one, and so do we.
			 * (iwx: this overrides IWX_UCODE_TLV_PAN; kept as is.)
			 */
			sc->sc_capaflags = (int)le32toh(*(const uint32_t *)tlv_data);
			break;
		case IWX_UCODE_TLV_CSCHEME:
			err = iwx_store_cscheme(sc, tlv_data, tlv_len);
			if (err)
				goto parse_out;
			break;
		case IWX_UCODE_TLV_NUM_OF_CPU: {
			uint32_t num_cpu;
			if (tlv_len != sizeof(uint32_t)) {
				err = EINVAL;
				goto parse_out;
			}
			num_cpu = le32toh(*(const uint32_t *)tlv_data);
			if (num_cpu < 1 || num_cpu > 2) {
				err = EINVAL;
				goto parse_out;
			}
			break;
		}
		case IWX_UCODE_TLV_SEC_RT:
			err = iwx_firmware_store_section(sc,
			    IWX_UCODE_TYPE_REGULAR, tlv_data, tlv_len);
			if (err)
				goto parse_out;
			break;
		case IWX_UCODE_TLV_SEC_INIT:
			err = iwx_firmware_store_section(sc,
			    IWX_UCODE_TYPE_INIT, tlv_data, tlv_len);
			if (err)
				goto parse_out;
			break;
		case IWX_UCODE_TLV_SEC_WOWLAN:
			err = iwx_firmware_store_section(sc,
			    IWX_UCODE_TYPE_WOW, tlv_data, tlv_len);
			if (err)
				goto parse_out;
			break;
		case IWX_UCODE_TLV_DEF_CALIB:
			if (tlv_len != sizeof(struct iwx_tlv_calib_data)) {
				err = EINVAL;
				goto parse_out;
			}
			err = iwx_set_default_calib(sc, tlv_data);
			if (err)
				goto parse_out;
			break;
		case IWX_UCODE_TLV_PHY_SKU:
			if (tlv_len != sizeof(uint32_t)) {
				err = EINVAL;
				goto parse_out;
			}
			sc->sc_fw_phy_config = (int)le32toh(*(const uint32_t *)tlv_data);
			break;

		case IWX_UCODE_TLV_API_CHANGES_SET: {
			const struct iwx_ucode_api *api;
			uint32_t idx, i;
			if (tlv_len != sizeof(*api)) {
				err = EINVAL;
				goto parse_out;
			}
			api = (const struct iwx_ucode_api *)tlv_data;
			idx = le32toh(api->api_index);
			if (idx >= howmany(IWX_NUM_UCODE_TLV_API, 32)) {
				err = EINVAL;
				goto parse_out;
			}
			for (i = 0; i < 32; i++) {
				if ((le32toh(api->api_flags) & (1u << i)) == 0)
					continue;
				setbit(sc->sc_ucode_api, i + (32 * idx));
			}
			break;
		}

		case IWX_UCODE_TLV_ENABLED_CAPABILITIES: {
			const struct iwx_ucode_capa *capa;
			uint32_t idx, i;
			if (tlv_len != sizeof(*capa)) {
				err = EINVAL;
				goto parse_out;
			}
			capa = (const struct iwx_ucode_capa *)tlv_data;
			idx = le32toh(capa->api_index);
			if (idx >= howmany(IWX_NUM_UCODE_TLV_CAPA, 32)) {
				err = EINVAL;
				goto parse_out;
			}
			for (i = 0; i < 32; i++) {
				if ((le32toh(capa->api_capa) & (1u << i)) == 0)
					continue;
				setbit(sc->sc_enabled_capa, i + (32 * idx));
			}
			break;
		}

		case IWX_UCODE_TLV_SDIO_ADMA_ADDR:
		case IWX_UCODE_TLV_FW_GSCAN_CAPA:
			/* ignore, not used by current driver */
			break;

		case IWX_UCODE_TLV_SEC_RT_USNIFFER:
			err = iwx_firmware_store_section(sc,
			    IWX_UCODE_TYPE_REGULAR_USNIFFER, tlv_data,
			    tlv_len);
			if (err)
				goto parse_out;
			break;

		case IWX_UCODE_TLV_PAGING:
			if (tlv_len != sizeof(uint32_t)) {
				err = EINVAL;
				goto parse_out;
			}
			break;

		case IWX_UCODE_TLV_N_SCAN_CHANNELS:
			if (tlv_len != sizeof(uint32_t)) {
				err = EINVAL;
				goto parse_out;
			}
			sc->sc_capa_n_scan_channels =
			    (int)le32toh(*(const uint32_t *)tlv_data);
			if (sc->sc_capa_n_scan_channels > IWX_MAX_SCAN_CHANNELS) {
				err = ERANGE;
				goto parse_out;
			}
			break;

		case IWX_UCODE_TLV_FW_VERSION:
			if (tlv_len != sizeof(uint32_t) * 3) {
				err = EINVAL;
				goto parse_out;
			}

			iwx_fw_version_str(sc->sc_fwver, sizeof(sc->sc_fwver),
			    le32toh(((const uint32_t *)tlv_data)[0]),
			    le32toh(((const uint32_t *)tlv_data)[1]),
			    le32toh(((const uint32_t *)tlv_data)[2]));
			break;

		case IWX_UCODE_TLV_FW_DBG_DEST: {
			const struct iwx_fw_dbg_dest_tlv_v1 *dest_v1 = NULL;

			/* D-FW2 */
			if (tlv_len < offsetof(struct iwx_fw_dbg_dest_tlv_v1,
			    reg_ops)) {
				err = EINVAL;
				goto parse_out;
			}
			fw->dbg_dest_ver = tlv_data;
			if (*fw->dbg_dest_ver != 0) {
				err = EINVAL;
				goto parse_out;
			}

			if (fw->dbg_dest_tlv_init)
				break;
			fw->dbg_dest_tlv_init = 1;

			dest_v1 = (const void *)tlv_data;
			fw->dbg_dest_tlv_v1 = dest_v1;
			fw->n_dest_reg = (uint8_t)((tlv_len -
			    offsetof(struct iwx_fw_dbg_dest_tlv_v1, reg_ops)) /
			    sizeof(dest_v1->reg_ops[0]));
			break;
		}

		case IWX_UCODE_TLV_FW_DBG_CONF: {
			const struct iwx_fw_dbg_conf_tlv *conf =
			    (const void *)tlv_data;

			if (tlv_len < sizeof(*conf))	/* D-FW2 */
				break;
			if (!fw->dbg_dest_tlv_init ||
			    conf->id >= nitems(fw->dbg_conf_tlv) ||
			    fw->dbg_conf_tlv[conf->id] != NULL)
				break;

			fw->dbg_conf_tlv[conf->id] = conf;
			fw->dbg_conf_tlv_len[conf->id] = tlv_len;
			break;
		}

		case IWX_UCODE_TLV_UMAC_DEBUG_ADDRS: {
			const struct iwx_umac_debug_addrs *dbg_ptrs =
				(const void *)tlv_data;

			if (tlv_len != sizeof(*dbg_ptrs)) {
				err = EINVAL;
				goto parse_out;
			}
			if (sc->sc_device_family < IWX_DEVICE_FAMILY_22000)
				break;
			sc->sc_uc.uc_umac_error_event_table =
				le32toh(dbg_ptrs->error_info_addr) &
				~IWX_FW_ADDR_CACHE_CONTROL;
			sc->sc_uc.error_event_table_tlv_status |=
				IWX_ERROR_EVENT_TABLE_UMAC;
			break;
		}

		case IWX_UCODE_TLV_LMAC_DEBUG_ADDRS: {
			const struct iwx_lmac_debug_addrs *dbg_ptrs =
				(const void *)tlv_data;

			if (tlv_len != sizeof(*dbg_ptrs)) {
				err = EINVAL;
				goto parse_out;
			}
			if (sc->sc_device_family < IWX_DEVICE_FAMILY_22000)
				break;
			sc->sc_uc.uc_lmac_error_event_table[0] =
				le32toh(dbg_ptrs->error_event_table_ptr) &
				~IWX_FW_ADDR_CACHE_CONTROL;
			sc->sc_uc.error_event_table_tlv_status |=
				IWX_ERROR_EVENT_TABLE_LMAC1;
			break;
		}

		case IWX_UCODE_TLV_FW_MEM_SEG:
			break;

		case IWX_UCODE_TLV_IML:
			if (fw->iml != NULL) {
				osdep_free(fw->iml, fw->iml_len);
				fw->iml = NULL;
				fw->iml_len = 0;
			}
			if (tlv_len == 0)
				break;
			fw->iml = osdep_malloc(tlv_len);
			if (fw->iml == NULL) {
				err = ENOMEM;
				goto parse_out;
			}
			memcpy(fw->iml, tlv_data, tlv_len);
			fw->iml_len = tlv_len;
			break;

		case IWX_UCODE_TLV_CMD_VERSIONS:
			if (tlv_len % sizeof(struct iwx_fw_cmd_version)) {
				tlv_len /= sizeof(struct iwx_fw_cmd_version);
				tlv_len *= sizeof(struct iwx_fw_cmd_version);
			}
			if (sc->n_cmd_versions != 0) {
				err = EINVAL;
				goto parse_out;
			}
			if (tlv_len > sizeof(sc->cmd_versions)) {
				err = EINVAL;
				goto parse_out;
			}
			memcpy(&sc->cmd_versions[0], tlv_data, tlv_len);
			sc->n_cmd_versions = (int)(tlv_len /
			    sizeof(struct iwx_fw_cmd_version));
			break;

		case IWX_UCODE_TLV_FW_RECOVERY_INFO:
			break;

		case IWX_UCODE_TLV_PNVM_DATA:
			if (fw->pnvm != NULL || tlv_len == 0)
				break;
			fw->pnvm = osdep_malloc(tlv_len);
			if (fw->pnvm == NULL) {
				err = ENOMEM;
				goto parse_out;
			}
			memcpy(fw->pnvm, tlv_data, tlv_len);
			fw->pnvm_len = tlv_len;
			break;

		case IWX_UCODE_TLV_FW_FSEQ_VERSION:
		case IWX_UCODE_TLV_PHY_INTEGRATION_VERSION:
		case IWX_UCODE_TLV_FW_NUM_STATIONS:
		case IWX_UCODE_TLV_FW_NUM_BEACONS:
			break;

		/* undocumented TLVs found in iwx-cc-a0-46 image */
		case 58:
		case 0x1000003:
		case 0x1000004:
			break;

		/* undocumented TLVs found in iwx-cc-a0-48 image */
		case 0x1000000:
		case 0x1000002:
			break;

		case IWX_UCODE_TLV_TYPE_DEBUG_INFO:
		case IWX_UCODE_TLV_TYPE_BUFFER_ALLOCATION:
		case IWX_UCODE_TLV_TYPE_HCMD:
		case IWX_UCODE_TLV_TYPE_REGIONS:
		case IWX_UCODE_TLV_TYPE_TRIGGERS:
		case IWX_UCODE_TLV_TYPE_CONF_SET:
		case IWX_UCODE_TLV_SEC_TABLE_ADDR:
		case IWX_UCODE_TLV_D3_KEK_KCK_ADDR:
		case IWX_UCODE_TLV_CURRENT_PC:
			break;

		/* undocumented TLV found in iwx-cc-a0-67 image */
		case 0x100000b:
			break;

		/* undocumented TLV found in iwx-ty-a0-gf-a0-73 image */
		case 0x101:
			break;

		/* undocumented TLV found in iwx-ty-a0-gf-a0-77 image */
		case 0x100000c:
			break;

		/* undocumented TLVs found in iwx-bz-a0-gf-a0-92 image */
		case 65:
		case 69:
		case 1092:
			break;

		default:
			err = EINVAL;
			goto parse_out;
		}

		/*
		 * Check for size_t overflow and ignore missing padding at
		 * end of firmware file.
		 */
		if (roundup(tlv_len, 4) > len)
			break;

		len -= roundup(tlv_len, 4);
		data += roundup(tlv_len, 4);
	}

 parse_out:
	if (err) {
		IWX_ERR(sc, "firmware parse error %d, section type %u\n",
		    err, (unsigned)tlv_type);
	}
	return err;
}

/*
 * iwx_read_firmware(): load the file through the OS backend and parse it.
 * Parsed state is cached (fw_status DONE) exactly like iwx.
 */
int
iwx_read_firmware(struct iwx_softc *sc)
{
	struct iwx_fw_info *fw = &sc->sc_fw;
	uint8_t *raw = NULL;
	size_t rawlen = 0;
	int err;

	if (fw->fw_status == IWX_FW_STATUS_DONE)
		return 0;

	/* Serialized by the device mutex: no INPROGRESS sleeping needed. */
	fw->fw_status = IWX_FW_STATUS_INPROGRESS;

	if (fw->fw_rawdata != NULL)
		iwx_fw_info_free(sc);

	err = osdep_fw_load(sc->sc_os, sc->sc_fwname, &raw, &rawlen);
	if (err) {
		IWX_ERR(sc, "could not read firmware %s (error %d)\n",
		    sc->sc_fwname, err);
		goto out;
	}

	IWX_LOG(sc, "using firmware %s (%u bytes)\n", sc->sc_fwname,
	    (unsigned)rawlen);

	err = iwx_parse_firmware(sc, raw, rawlen);
 out:
	if (err) {
		fw->fw_status = IWX_FW_STATUS_NONE;
		if (fw->fw_rawdata != NULL)
			iwx_fw_info_free(sc);
		else if (raw != NULL)
			osdep_fw_release(sc->sc_os, raw, rawlen);
	} else
		fw->fw_status = IWX_FW_STATUS_DONE;

	return err;
}

int
iwx_get_num_sections(const struct iwx_fw_sects *fws, int start)
{
	int i = 0;

	while (start < fws->fw_count &&
	       fws->fw_sect[start].fws_devoff != IWX_CPU1_CPU2_SEPARATOR_SECTION &&
	       fws->fw_sect[start].fws_devoff != IWX_PAGING_SEPARATOR_SECTION) {
		start++;
		i++;
	}

	return i;
}
