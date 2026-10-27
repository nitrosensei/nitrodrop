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
 * Host-side test harness: mock osdep + a small AX210-family device model.
 *
 * DMA addresses are identity mapped (paddr == (uintptr_t)vaddr), so the
 * device model can follow every pointer the driver programs, exactly like
 * the real device would through the IOMMU.
 *
 * The model is deliberately strict: it checks what iwx programs (context
 * info, IML registers, TFD layout, doorbells) and records every host
 * command, then answers like firmware would (ALIVE, command responses,
 * INIT_COMPLETE, PNVM_INIT_COMPLETE, NVM_GET_INFO, MCC_UPDATE).
 * Interrupts are delivered when the driver waits (osdep_event_wait),
 * i.e. at the points where real hardware interrupts would interleave.
 */
#ifndef AIRDROP_TEST_MOCK_H
#define AIRDROP_TEST_MOCK_H

#include <stdio.h>
#include <stdlib.h>
#include "../core/iwx_var.h"

/* ---- mini test framework ------------------------------------------------ */
extern int g_failures;
extern int g_checks;
#define CHECK(cond) do {						\
	g_checks++;							\
	if (!(cond)) {							\
		g_failures++;						\
		printf("  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);\
	}								\
} while (0)
#define CHECK_EQ(a, b) do {						\
	unsigned long long _a = (unsigned long long)(a);		\
	unsigned long long _b = (unsigned long long)(b);		\
	g_checks++;							\
	if (_a != _b) {							\
		g_failures++;						\
		printf("  FAIL %s:%d: %s == %s (0x%llx != 0x%llx)\n",	\
		    __FILE__, __LINE__, #a, #b, _a, _b);		\
	}								\
} while (0)

/* ---- recorded host commands -------------------------------------------- */
#define SIM_MAX_CMDS	128
struct sim_cmd {
	uint8_t group;
	uint8_t opcode;
	uint8_t version;
	uint8_t idx;
	uint16_t paylen;
	uint8_t payload[64];	/* first bytes */
};

/* ---- device model state ------------------------------------------------- */
struct sim {
	struct iwx_softc *sc;		/* for interrupt delivery */
	uint8_t regs[0x10000];		/* BAR0 */
	uint32_t prph_waddr, prph_raddr;
	uint32_t prph_addr[256];	/* tiny PRPH store */
	uint32_t prph_val[256];
	int nprph;
	uint32_t pci_cfg[64];

	/* firmware state */
	int booted;			/* CPU_INIT_RUN seen, ALIVE sent */
	int boots;
	int alive_ver;			/* 4..7, picks ALIVE layout */
	uint32_t alive_status;		/* IWX_ALIVE_STATUS_OK */
	uint32_t sku_id[3];
	int pnvm_doorbells;
	int nvm_v4;			/* answer NVM_GET_INFO in v4 layout */
	int mcc_v4;			/* answer MCC_UPDATE in v4 layout */
	int lar_enabled;		/* NVM reports LAR */
	int fail_cmd_group, fail_cmd_opcode;	/* -1: never */
	int drop_responses;		/* don't answer commands */
	uint8_t mac[6];

	/* what the driver programmed at boot (validated copies) */
	struct iwx_context_info_gen3 ctxt;
	struct iwx_prph_scratch_ctrl_cfg scratch_ctrl;
	uint64_t lmac0, umac0, paging0;
	int ctxt_ok;
	char ctxt_err[160];

	/* command queue */
	int cmd_read;			/* next TFD index to consume */
	struct sim_cmd cmds[SIM_MAX_CMDS];
	int ncmds;
	int tfd_errors;

	/* RX */
	int rx_write;			/* next RB the "device" fills */
	uint32_t last_frbdcb_widx;

	/* pending interrupt causes */
	uint32_t fh_cause, hw_cause;
	int irq_pending;
	int intr_delivered;

	/* osdep bookkeeping */
	uint64_t now_us;
	long mallocs, frees;
	long dma_allocs, dma_frees;
	int lock_depth;
	int lock_errors;
	int event_signaled[OSDEP_EV_COUNT];
	int fatal_count;
	int fatal_last;

	/* firmware file served by osdep_fw_load() */
	uint8_t *fw_data;
	size_t fw_len;
	int fw_loads;

	/* frames delivered by osdep_rx_frame() */
	int rx_frames;
	struct iwx_rx_meta last_meta;
	uint8_t last_frame[512];
	uint32_t last_frame_len;
	int quiet;			/* suppress osdep_log output */
};

extern struct sim g_sim;

void sim_reset(void);
void sim_set_pci_ids(uint16_t dev, uint16_t sub);
void sim_raise(uint32_t fh, uint32_t hw);
void sim_service_interrupts(void);
/* put one packet into the next RX buffer and raise FH Q0 */
void sim_rx_packet(uint8_t group, uint8_t code, uint8_t qid, uint8_t idx,
    const void *payload, uint32_t paylen);
int sim_find_cmd(uint8_t group, uint8_t opcode);

/* synthetic firmware image builder (test_fw.c) */
uint8_t *fwgen_build(size_t *len, int with_iml);

#endif
