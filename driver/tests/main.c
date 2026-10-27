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


/* Host test runner for the portable iwx core. */

#include "mock.h"
#include "../core/iwx_core.h"
#include "../include/airdrop_ioctl.h"

int g_failures;
int g_checks;

void test_fw_synthetic(void);
void test_fw_real(const char *path);
void test_attach_reject(void);
void test_bringup(const uint8_t *fw, size_t fwlen, const char *label);
void test_failures(const uint8_t *fw, size_t fwlen);
void test_cmd_queue(const uint8_t *fw, size_t fwlen);
void test_rx_ring(const uint8_t *fw, size_t fwlen);

static uint8_t *
slurp(const char *path, size_t *len)
{
	FILE *f = fopen(path, "rb");
	uint8_t *p = NULL;
	long n;

	if (f == NULL)
		return NULL;
	if (fseek(f, 0, SEEK_END) == 0 && (n = ftell(f)) > 0) {
		fseek(f, 0, SEEK_SET);
		p = malloc((size_t)n);
		if (p != NULL && fread(p, 1, (size_t)n, f) == (size_t)n)
			*len = (size_t)n;
		else {
			free(p);
			p = NULL;
		}
	}
	fclose(f);
	return p;
}

static void
test_layouts(void)
{
	printf("test_layouts\n");
	/* device ABI structures (sizes from iwlwifi / iwx) */
	CHECK_EQ(sizeof(struct iwx_context_info_gen3), 0x68);
	CHECK_EQ(sizeof(struct iwx_tfh_tb), 10);
	CHECK_EQ(sizeof(struct iwx_tfh_tfd), 2 + 10 * IWX_TFH_NUM_TBS + 4);
	CHECK_EQ(sizeof(struct iwx_cmd_header_wide), 8);
	CHECK_EQ(sizeof(struct iwx_rx_transfer_desc), 16);
	CHECK_EQ(sizeof(struct iwx_rx_completion_desc), 32);
	CHECK_EQ(sizeof(struct iwx_rx_mpdu_desc), 64);
	CHECK_EQ(IWX_RX_DESC_SIZE_V1, 48);
	CHECK_EQ(sizeof(struct iwx_context_info_dram),
	    3 * IWX_MAX_DRAM_ENTRY * 8);
	/* user/kernel ABI */
	CHECK_EQ(sizeof(AIRDROP_INFO), 144);
	CHECK_EQ(sizeof(AIRDROP_START_PARAMS), 16);
	CHECK_EQ(sizeof(AIRDROP_RX_HEADER), 48);
	CHECK_EQ(sizeof(AIRDROP_TX_HEADER), 16);
	CHECK_EQ(AIRDROP_STATE_FW_READY, IWX_STATE_FW_READY);
}

int
main(int argc, char **argv)
{
	const char *fwpath = argc > 1 ? argv[1] :
	    "../firmware/iwlwifi-so-a0-hr-b0-77.ucode";
	uint8_t *real, *syn;
	size_t rlen = 0, slen = 0;

	test_layouts();
	test_fw_synthetic();
	test_fw_real(fwpath);
	test_attach_reject();

	syn = fwgen_build(&slen, 1);
	test_bringup(syn, slen, "synthetic firmware");
	test_failures(syn, slen);
	test_cmd_queue(syn, slen);
	test_rx_ring(syn, slen);

	real = slurp(fwpath, &rlen);
	if (real != NULL) {
		test_bringup(real, rlen, "real firmware image");
		free(real);
	} else
		printf("test_bringup (real firmware): SKIP\n");
	free(syn);

	printf("\n%d checks, %d failures\n", g_checks, g_failures);
	return g_failures ? 1 : 0;
}
