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

#include <stddef.h>
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
void test_radio(const uint8_t *fw, size_t fwlen);
void test_queues(void);

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
	CHECK_EQ(sizeof(AIRDROP_INFO), 176);
	CHECK_EQ(offsetof(AIRDROP_INFO, RxBadFcs), AIRDROP_INFO_V1_SIZE);
	CHECK_EQ(sizeof(AIRDROP_START_PARAMS), 24);
	CHECK_EQ(offsetof(AIRDROP_START_PARAMS, BeaconIntervalTu),
	    AIRDROP_START_PARAMS_V1_SIZE);
	CHECK_EQ(sizeof(AIRDROP_RX_HEADER), 64);
	CHECK_EQ(offsetof(AIRDROP_RX_HEADER, AmpduRef), 48);
	CHECK_EQ(sizeof(AIRDROP_TX_HEADER), 16);
	CHECK_EQ(sizeof(AIRDROP_TX_RESULT), 24);
	CHECK_EQ(AIRDROP_STATE_FW_READY, IWX_STATE_FW_READY);
	CHECK_EQ(AIRDROP_STATE_RADIO_ON, IWX_STATE_RADIO_ON);
	/* flag / enum values the KMDF layer passes through unchanged */
	CHECK_EQ(AIRDROP_START_F_LISTENER_ONLY, IWX_RADIO_F_LISTENER_ONLY);
	CHECK_EQ(AIRDROP_START_F_KEEP_BAD_FCS, IWX_RADIO_F_KEEP_BAD_FCS);
	CHECK_EQ(AIRDROP_START_F_NO_PROMISC, IWX_RADIO_F_NO_PROMISC);
	CHECK_EQ(AIRDROP_TX_F_NOACK, IWX_TX_F_NOACK);
	CHECK_EQ(AIRDROP_TX_F_RATE_HT, IWX_TX_F_RATE_HT);
	CHECK_EQ(AIRDROP_TX_F_ASSIGN_SEQ, IWX_TX_F_ASSIGN_SEQ);
	CHECK_EQ(AIRDROP_TX_F_SGI, IWX_TX_F_SGI);
	CHECK_EQ(AIRDROP_TX_F_HIGH_PRI, IWX_TX_F_HIGH_PRI);
	CHECK_EQ(AIRDROP_TXST_SUCCESS, IWX_TXST_SUCCESS);
	CHECK_EQ(AIRDROP_TXST_FAILED, IWX_TXST_FAILED);
	CHECK_EQ(AIRDROP_TXST_FLUSHED, IWX_TXST_FLUSHED);
	CHECK_EQ(AIRDROP_TXST_NO_STATUS, IWX_TXST_NO_STATUS);
	CHECK_EQ(AIRDROP_TXR_F_NOACK_NOT_HONORED, IWX_TXR_F_NOACK_NOT_HONORED);
	CHECK_EQ(AIRDROP_TXR_F_ACK_POLICY_SET, IWX_TXR_F_ACK_POLICY_SET);
	CHECK_EQ(AIRDROP_TXR_F_SEQ_ASSIGNED, IWX_TXR_F_SEQ_ASSIGNED);
	CHECK_EQ(AIRDROP_RADIO_MODE_IBSS, IWX_RADIO_MODE_IBSS);
	CHECK_EQ(AIRDROP_RX_ENC_EHT, IWX_RXMETA_ENC_EHT);
	CHECK_EQ(AIRDROP_TX_MAX_FRAME, IWX_TX_MAX_FRAME);
	CHECK_EQ(IWX_TX_PAYLOAD_SLOT >= IWX_TX_MAX_FRAME, 1);
	CHECK_EQ(IWX_TX_PAYLOAD_SLOT % 64, 0);
	/* struct iwx_tx_cmd_gen3 without the 802.11 header */
	CHECK_EQ(sizeof(struct iwx_tx_cmd_gen3), 28);
	CHECK_EQ(sizeof(struct iwx_tx_resp), 44);	/* incl. status[0] */
	CHECK_EQ(sizeof(struct iwx_mac_ctx_cmd), 148);
}

int
main(int argc, char **argv)
{
	const char *fwpath = argc > 1 ? argv[1] :
	    "../firmware/iwlwifi-so-a0-hr-b0-77.ucode";
	uint8_t *real, *syn;
	size_t rlen = 0, slen = 0;

	setvbuf(stdout, NULL, _IONBF, 0);
	test_layouts();
	test_fw_synthetic();
	test_fw_real(fwpath);
	test_attach_reject();
	test_queues();

	syn = fwgen_build(&slen, 1);
	test_bringup(syn, slen, "synthetic firmware");
	test_failures(syn, slen);
	test_cmd_queue(syn, slen);
	test_rx_ring(syn, slen);

	real = slurp(fwpath, &rlen);
	if (real != NULL) {
		test_bringup(real, rlen, "real firmware image");
		test_radio(real, rlen);
		free(real);
	} else
		printf("test_bringup (real firmware): SKIP\n");
	free(syn);

	printf("\n%d checks, %d failures\n", g_checks, g_failures);
	return g_failures ? 1 : 0;
}
