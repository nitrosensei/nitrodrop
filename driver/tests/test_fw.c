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

/* Firmware TLV parsing: synthetic images + the real linux-firmware file. */

#include "mock.h"

/* ---- synthetic image builder -------------------------------------------- */

struct fwbuf {
	uint8_t *p;
	size_t len, cap;
};

static void
fb_put(struct fwbuf *b, const void *d, size_t n)
{
	if (b->len + n > b->cap) {
		b->cap = (b->len + n) * 2 + 256;
		b->p = realloc(b->p, b->cap);
	}
	memcpy(b->p + b->len, d, n);
	b->len += n;
}

static void
fb_tlv(struct fwbuf *b, uint32_t type, const void *d, uint32_t n)
{
	static const uint8_t pad[4] = { 0 };
	fb_put(b, &type, 4);
	fb_put(b, &n, 4);
	if (n)
		fb_put(b, d, n);
	if (n % 4)
		fb_put(b, pad, 4 - n % 4);
}

static void
fb_sec(struct fwbuf *b, uint32_t devoff, uint32_t len, uint8_t fill)
{
	uint8_t tmp[300];
	uint32_t i;

	memcpy(tmp, &devoff, 4);
	for (i = 0; i < len && i + 4 < sizeof(tmp); i++)
		tmp[4 + i] = (uint8_t)(fill + i);
	fb_tlv(b, IWX_UCODE_TLV_SEC_RT, tmp, 4 + len);
}

/*
 * Layout: LMAC x2, separator, UMAC x1, paging separator, paging x1.
 * Capabilities match what So+Hr firmware advertises for the init path.
 */
uint8_t *
fwgen_build(size_t *len, int with_iml)
{
	struct fwbuf b = { 0 };
	struct iwx_tlv_ucode_header h;
	struct iwx_fw_cmd_version cv[3];
	struct iwx_ucode_api api;
	struct iwx_ucode_capa capa;
	uint32_t v, ver3[3];
	uint8_t iml[100];
	int i;

	memset(&h, 0, sizeof(h));
	h.magic = IWX_TLV_UCODE_MAGIC;
	memcpy(h.human_readable, "synthetic", 9);
	h.ver = (77u << 24) | (1u << 16) | (77u << 8);	/* overridden below */
	fb_put(&b, &h, sizeof(h));

	v = 1;
	fb_tlv(&b, IWX_UCODE_TLV_NUM_OF_CPU, &(uint32_t){ 2 }, 4);
	(void)v;

	fb_sec(&b, 0x00404000, 200, 0x10);	/* LMAC 0 */
	fb_sec(&b, 0x00800000, 100, 0x20);	/* LMAC 1 */
	fb_sec(&b, IWX_CPU1_CPU2_SEPARATOR_SECTION, 0, 0);
	fb_sec(&b, 0x00c00000, 150, 0x30);	/* UMAC 0 */
	fb_sec(&b, IWX_PAGING_SEPARATOR_SECTION, 0, 0);
	fb_sec(&b, 0x00000000, 120, 0x40);	/* paging 0 */

	if (with_iml) {
		for (i = 0; i < (int)sizeof(iml); i++)
			iml[i] = (uint8_t)(0xa0 + i);
		fb_tlv(&b, IWX_UCODE_TLV_IML, iml, sizeof(iml));
	}

	/* ALIVE notif v6, TX_CMD notif v8 (-> rate_n_flags v2), SCAN_REQ v14 */
	cv[0].cmd = IWX_ALIVE; cv[0].group = IWX_LEGACY_GROUP;
	cv[0].cmd_ver = 0; cv[0].notif_ver = 6;
	cv[1].cmd = IWX_TX_CMD; cv[1].group = IWX_LONG_GROUP;
	cv[1].cmd_ver = 8; cv[1].notif_ver = 8;
	cv[2].cmd = IWX_SCAN_REQ_UMAC; cv[2].group = IWX_LONG_GROUP;
	cv[2].cmd_ver = 14; cv[2].notif_ver = 0;
	fb_tlv(&b, IWX_UCODE_TLV_CMD_VERSIONS, cv, sizeof(cv));

	api.api_index = IWX_UCODE_TLV_API_REGULATORY_NVM_INFO / 32;
	api.api_flags = 1u << (IWX_UCODE_TLV_API_REGULATORY_NVM_INFO % 32);
	fb_tlv(&b, IWX_UCODE_TLV_API_CHANGES_SET, &api, sizeof(api));
	api.api_index = IWX_UCODE_TLV_API_REDUCED_SCAN_CONFIG / 32;
	api.api_flags = 1u << (IWX_UCODE_TLV_API_REDUCED_SCAN_CONFIG % 32);
	fb_tlv(&b, IWX_UCODE_TLV_API_CHANGES_SET, &api, sizeof(api));

	capa.api_index = 0;
	capa.api_capa = (1u << IWX_UCODE_TLV_CAPA_LAR_SUPPORT) |
	    (1u << IWX_UCODE_TLV_CAPA_DQA_SUPPORT) |
	    (1u << IWX_UCODE_TLV_CAPA_LAR_MULTI_MCC);
	fb_tlv(&b, IWX_UCODE_TLV_ENABLED_CAPABILITIES, &capa, sizeof(capa));
	capa.api_index = 2;
	capa.api_capa = (1u << (IWX_UCODE_TLV_CAPA_CT_KILL_BY_FW % 32)) |
	    (1u << (IWX_UCODE_TLV_CAPA_MCC_UPDATE_11AX_SUPPORT % 32));
	fb_tlv(&b, IWX_UCODE_TLV_ENABLED_CAPABILITIES, &capa, sizeof(capa));

	v = (3u << IWX_FW_PHY_CFG_TX_CHAIN_POS) |
	    (3u << IWX_FW_PHY_CFG_RX_CHAIN_POS);
	fb_tlv(&b, IWX_UCODE_TLV_PHY_SKU, &v, 4);

	ver3[0] = 77; ver3[1] = 0xf92b5fed; ver3[2] = 0;
	fb_tlv(&b, IWX_UCODE_TLV_FW_VERSION, ver3, sizeof(ver3));

	*len = b.len;
	return b.p;
}

static uint8_t *
read_file(const char *path, size_t *len)
{
	FILE *f = fopen(path, "rb");
	uint8_t *p;
	long n;

	if (f == NULL)
		return NULL;
	fseek(f, 0, SEEK_END);
	n = ftell(f);
	fseek(f, 0, SEEK_SET);
	p = malloc((size_t)n);
	if (p == NULL || fread(p, 1, (size_t)n, f) != (size_t)n) {
		fclose(f);
		free(p);
		return NULL;
	}
	fclose(f);
	*len = (size_t)n;
	return p;
}

/* parse a private copy (iwx_parse_firmware takes ownership) */
static int
parse_copy(struct iwx_softc *sc, const uint8_t *img, size_t len)
{
	uint8_t *copy = malloc(len ? len : 1);
	memcpy(copy, img, len);
	return iwx_parse_firmware(sc, copy, len);
}

static struct iwx_softc *
new_sc(void)
{
	struct iwx_softc *sc = calloc(1, sizeof(*sc));
	sc->sc_device_family = IWX_DEVICE_FAMILY_AX210;
	sc->sc_fwname = "test";
	return sc;
}

static void
free_sc(struct iwx_softc *sc)
{
	iwx_fw_info_free(sc);
	free(sc);
}

void
test_fw_synthetic(void)
{
	struct iwx_softc *sc;
	struct iwx_fw_sects *fws;
	uint8_t *img, *bad;
	size_t len;
	int err;

	printf("test_fw_synthetic\n");
	sim_reset();
	img = fwgen_build(&len, 1);

	sc = new_sc();
	err = parse_copy(sc, img, len);
	CHECK_EQ(err, 0);
	fws = &sc->sc_fw.fw_sects[IWX_UCODE_TYPE_REGULAR];
	CHECK_EQ(fws->fw_count, 6);
	CHECK_EQ(iwx_get_num_sections(fws, 0), 2);		/* LMAC */
	CHECK_EQ(iwx_get_num_sections(fws, 3), 1);		/* UMAC */
	CHECK_EQ(iwx_get_num_sections(fws, 5), 1);		/* paging */
	CHECK_EQ(fws->fw_sect[0].fws_devoff, 0x00404000);
	CHECK_EQ(fws->fw_sect[0].fws_len, 200);
	CHECK_EQ(((const uint8_t *)fws->fw_sect[0].fws_data)[0], 0x10);
	CHECK_EQ(sc->sc_fw.iml_len, 100);
	CHECK(sc->sc_fw.iml != NULL && sc->sc_fw.iml[0] == 0xa0);
	CHECK_EQ(sc->n_cmd_versions, 3);
	CHECK_EQ(iwx_lookup_notif_ver(sc, IWX_LEGACY_GROUP, IWX_ALIVE), 6);
	CHECK_EQ(iwx_lookup_cmd_ver(sc, IWX_LONG_GROUP, IWX_SCAN_REQ_UMAC), 14);
	CHECK_EQ(iwx_lookup_cmd_ver(sc, 0x55, 0x55), IWX_FW_CMD_VER_UNKNOWN);
	CHECK(isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_REGULATORY_NVM_INFO));
	CHECK(isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_REDUCED_SCAN_CONFIG));
	CHECK(isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_DQA_SUPPORT));
	CHECK(isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_CT_KILL_BY_FW));
	CHECK(!isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_LAR_SUPPORT_V3));
	CHECK_EQ(sc->sc_fw_phy_config, (3u << 16) | (3u << 20));
	CHECK(strcmp(sc->sc_fwver, "77.f92b5fed.0") == 0);
	free_sc(sc);

	/* version string formatting: decimal minor below major 35 */
	{
		char buf[32];
		iwx_fw_version_str(buf, sizeof(buf), 34, 255, 3);
		CHECK(strcmp(buf, "34.255.3") == 0);
		iwx_fw_version_str(buf, 6, 77, 0xf92b5fed, 0);
		CHECK(strcmp(buf, "77.f9") == 0);	/* truncated, NUL */
	}

	/* negative: bad magic */
	sc = new_sc();
	bad = malloc(len);
	memcpy(bad, img, len);
	bad[4] ^= 0xff;
	CHECK_EQ(parse_copy(sc, bad, len), EINVAL);
	free_sc(sc);

	/* negative: non-zero first word */
	sc = new_sc();
	memcpy(bad, img, len);
	bad[0] = 1;
	CHECK_EQ(parse_copy(sc, bad, len), EINVAL);
	free_sc(sc);

	/* negative: file shorter than the header */
	sc = new_sc();
	CHECK_EQ(parse_copy(sc, img, 40), EINVAL);
	free_sc(sc);

	/* negative: TLV length beyond end of file */
	sc = new_sc();
	memcpy(bad, img, len);
	{
		uint32_t huge = 0x7fffffff;
		memcpy(bad + sizeof(struct iwx_tlv_ucode_header) + 4, &huge, 4);
	}
	CHECK_EQ(parse_copy(sc, bad, len), EINVAL);
	free_sc(sc);

	/* negative: unknown TLV type -> EINVAL like iwx */
	sc = new_sc();
	memcpy(bad, img, len);
	{
		uint32_t t = 0x7777;
		memcpy(bad + sizeof(struct iwx_tlv_ucode_header), &t, 4);
	}
	CHECK_EQ(parse_copy(sc, bad, len), EINVAL);
	free_sc(sc);

	/* truncated in the middle of the last TLV */
	sc = new_sc();
	CHECK_EQ(parse_copy(sc, img, len - 6), EINVAL);
	free_sc(sc);

	free(bad);
	free(img);
}

void
test_fw_real(const char *path)
{
	struct iwx_softc *sc;
	struct iwx_fw_sects *fws;
	uint8_t *img;
	size_t len;
	int lmac, umac, paging;

	printf("test_fw_real (%s)\n", path);
	img = read_file(path, &len);
	if (img == NULL) {
		printf("  SKIP: firmware not present (run tools\\fetch-firmware.ps1)\n");
		return;
	}
	sim_reset();
	sc = new_sc();
	CHECK_EQ(parse_copy(sc, img, len), 0);
	fws = &sc->sc_fw.fw_sects[IWX_UCODE_TYPE_REGULAR];
	lmac = iwx_get_num_sections(fws, 0);
	umac = iwx_get_num_sections(fws, lmac + 1);
	paging = iwx_get_num_sections(fws, lmac + umac + 2);
	printf("  fw %s: %d RT sections (lmac %d, umac %d, paging %d), "
	    "IML %u bytes, %d cmd versions\n", sc->sc_fwver, fws->fw_count,
	    lmac, umac, paging, (unsigned)sc->sc_fw.iml_len, sc->n_cmd_versions);
	printf("  ALIVE notif v%d, TX_CMD notif v%d, SCAN_REQ_UMAC v%d, "
	    "SCAN_CFG v%d, MCC_UPDATE notif v%d, phy_config 0x%08x\n",
	    iwx_lookup_notif_ver(sc, IWX_LEGACY_GROUP, IWX_ALIVE),
	    iwx_lookup_notif_ver(sc, IWX_LONG_GROUP, IWX_TX_CMD),
	    iwx_lookup_cmd_ver(sc, IWX_LONG_GROUP, IWX_SCAN_REQ_UMAC),
	    iwx_lookup_cmd_ver(sc, IWX_LONG_GROUP, IWX_SCAN_CFG_CMD),
	    iwx_lookup_notif_ver(sc, IWX_LONG_GROUP, IWX_MCC_UPDATE_CMD),
	    sc->sc_fw_phy_config);
	printf("  capa: dqa %d lar %d lar_multi_mcc %d mcc_11ax %d ct_kill %d "
	    "mld %d; api: nvm_info %d reduced_scan %d wifi_mcc %d\n",
	    isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_DQA_SUPPORT),
	    isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_LAR_SUPPORT),
	    isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_LAR_MULTI_MCC),
	    isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_MCC_UPDATE_11AX_SUPPORT),
	    isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_CT_KILL_BY_FW),
	    isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_MLD_API_SUPPORT),
	    isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_REGULATORY_NVM_INFO),
	    isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_REDUCED_SCAN_CONFIG),
	    isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_WIFI_MCC_UPDATE));

	/* facts about iwlwifi-so-a0-hr-b0-77 (core74_pv f92b5fed) */
	/* FW_VERSION TLV major is the API level (77); WHENCE "core74" is the branch */
	CHECK(strcmp(sc->sc_fwver, "77.f92b5fed.0") == 0);
	CHECK_EQ(fws->fw_count, 52);
	CHECK(sc->sc_fw.iml != NULL && sc->sc_fw.iml_len > 0);
	CHECK(lmac > 0 && umac > 0);
	CHECK(lmac <= 64 && umac <= 64 && paging <= 64);
	CHECK(sc->n_cmd_versions > 0);
	CHECK(isset(sc->sc_ucode_api, IWX_UCODE_TLV_API_REDUCED_SCAN_CONFIG));
	/* this image does not advertise DQA_SUPPORT -> iwx skips DQA_ENABLE */
	CHECK(!isset(sc->sc_enabled_capa, IWX_UCODE_TLV_CAPA_DQA_SUPPORT));
	free_sc(sc);
	free(img);
}
