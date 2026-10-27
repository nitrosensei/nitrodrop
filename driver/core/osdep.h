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
 * osdep.h - the only interface between the portable iwx core
 * (driver/core/*.c) and the operating system.
 *
 * Two implementations exist:
 *   driver/kmdf/osdep_kmdf.c   Windows kernel (KMDF), the real thing
 *   driver/tests/mock_osdep.c  user-mode simulator used by the host tests
 *
 * Rules for the core:
 *   - never include OS headers; only this file and the iwx headers;
 *   - every hardware access goes through osdep_read32/write32/write8;
 *   - every device-visible buffer comes from osdep_dma_alloc and its
 *     device address (dma->paddr) is the *logical* (IOMMU translated)
 *     address returned by the OS DMA API, never a CPU physical address;
 *   - blocking waits only through osdep_event_wait (PASSIVE_LEVEL);
 *   - code reached from the interrupt DPC must not sleep and must not use
 *     the NIC access lock (sc_nic_locks); see iwx_trans.c.
 */
#ifndef AIRDROP_OSDEP_H
#define AIRDROP_OSDEP_H

#include <stddef.h>
#if defined(_KERNEL_MODE)
/* km\crt has no <stdint.h>; the MSVC one clashes with km\crt headers. */
typedef signed char		int8_t;
typedef short			int16_t;
typedef int			int32_t;
typedef long long		int64_t;
typedef unsigned char		uint8_t;
typedef unsigned short		uint16_t;
typedef unsigned int		uint32_t;
typedef unsigned long long	uint64_t;
#ifndef _UINTPTR_T_DEFINED
typedef unsigned __int64	uintptr_t;
#define _UINTPTR_T_DEFINED
#endif
#else
#include <stdint.h>
#endif
#include <string.h>
#include <errno.h>	/* same POSIX values in km\crt and the UCRT */

#ifndef ENOTSUP
#define ENOTSUP		129
#endif
#ifndef ETIMEDOUT
#define ETIMEDOUT	138
#endif
#ifndef ENOBUFS
#define ENOBUFS		119
#endif

#if defined(_MSC_VER) && !defined(__cplusplus)
#ifndef inline
#define inline __inline
#endif
#endif

/* x86-64 and ARM64 Windows are little endian: byte order macros are no-ops. */
#define htole16(x)	((uint16_t)(x))
#define htole32(x)	((uint32_t)(x))
#define htole64(x)	((uint64_t)(x))
#define le16toh(x)	((uint16_t)(x))
#define le32toh(x)	((uint32_t)(x))
#define le64toh(x)	((uint64_t)(x))

#define nitems(_a)	(sizeof(_a) / sizeof((_a)[0]))
#define howmany(x, y)	(((x) + ((y) - 1)) / (y))
#define roundup(x, y)	((((x) + ((y) - 1)) / (y)) * (y))
#ifndef MIN
#define MIN(a, b)	(((a) < (b)) ? (a) : (b))
#endif
#ifndef MAX
#define MAX(a, b)	(((a) > (b)) ? (a) : (b))
#endif
#define NBBY		8
#define setbit(a, i)	(((uint8_t *)(a))[(i) / NBBY] |= (uint8_t)(1u << ((i) % NBBY)))
#define clrbit(a, i)	(((uint8_t *)(a))[(i) / NBBY] &= (uint8_t)~(1u << ((i) % NBBY)))
#define isset(a, i)	((((const uint8_t *)(a))[(i) / NBBY] & (1u << ((i) % NBBY))) != 0)

#define ETHER_ADDR_LEN		6
#define IEEE80211_NWID_LEN	32
#define IWX_PAGE_SIZE		4096

/* find last set bit, 1-based; 0 for 0 (BSD semantics) */
static inline int
iwx_fls(uint32_t x)
{
	int r = 0;
	while (x) {
		r++;
		x >>= 1;
	}
	return r;
}
#define fls(x)	iwx_fls((uint32_t)(x))
#define flsl(x)	iwx_fls((uint32_t)(x))

/*
 * A device-visible, physically contiguous, cache-coherent buffer.
 * Replaces OpenBSD's bus_dma "contig" allocation used by iwx.
 */
struct iwx_dma_info {
	uint64_t	paddr;		/* device (logical) address */
	void		*vaddr;		/* kernel virtual address, NX, RW */
	size_t		size;
	void		*os_handle;	/* WDFCOMMONBUFFER / mock record */
};

/* Wait channels (replace tsleep/wakeup channels used by iwx). */
enum osdep_event_id {
	OSDEP_EV_ALIVE = 0,	/* &sc->sc_uc        : ALIVE notification */
	OSDEP_EV_INIT,		/* &sc->sc_init_complete            */
	OSDEP_EV_CMD,		/* &ring->desc[idx]  : command completion */
	OSDEP_EV_COUNT
};

enum osdep_log_level {
	OSDEP_LOG_ERR = 0,
	OSDEP_LOG_WARN,
	OSDEP_LOG_INFO,
	OSDEP_LOG_DEBUG
};

/* Reasons passed to osdep_fatal(); backend schedules recovery at PASSIVE. */
enum osdep_fatal_reason {
	OSDEP_FATAL_FW_ERROR = 1,	/* SW_ERR / FH_ERR: firmware assert */
	OSDEP_FATAL_HW_ERROR,		/* HW_ERR / TOP_FATAL_ERR */
	OSDEP_FATAL_RFKILL,		/* rfkill state changed */
	OSDEP_FATAL_CT_KILL		/* critical temperature */
};

/* Metadata for one received 802.11 frame (filled by the core). */
struct iwx_rx_meta {
	uint32_t	rate_n_flags;	/* firmware rate_n_flags, raw */
	uint32_t	gp2;		/* device timestamp (usec), GP2 on air rise */
	uint64_t	tsf;		/* TSF on air rise, 0 if not valid */
	int8_t		rssi_dbm;	/* max(energy_a, energy_b) in dBm */
	uint8_t		channel;	/* firmware channel number */
	uint16_t	phy_info;
	uint32_t	status;		/* IWX_RX_MPDU_RES_STATUS_* */
	uint8_t		mac_context;
	uint8_t		flags;		/* IWX_RXMETA_F_* */
};
#define IWX_RXMETA_F_SHORTPRE	0x01
#define IWX_RXMETA_F_TSF_VALID	0x02

#ifdef __cplusplus
extern "C" {
#endif

/* ---- MMIO (BAR0) --------------------------------------------------- */
uint32_t osdep_read32(void *os, uint32_t off);
void	 osdep_write32(void *os, uint32_t off, uint32_t val);
void	 osdep_write8(void *os, uint32_t off, uint8_t val);
/* full barrier: orders MMIO and coherent DMA memory accesses */
void	 osdep_barrier(void *os);

/* ---- PCI configuration space --------------------------------------- */
uint32_t osdep_pci_read32(void *os, uint32_t off);
void	 osdep_pci_write32(void *os, uint32_t off, uint32_t val);

/* ---- time ------------------------------------------------------------ */
/* Busy-wait or sleep for 'us' microseconds. Callable at <= DISPATCH. */
void	 osdep_delay_us(uint32_t us);
/* Monotonic microsecond clock. */
uint64_t osdep_time_us(void);

/* ---- memory ---------------------------------------------------------- */
/* Zeroed non-paged, non-executable memory. NULL on failure. */
void	*osdep_malloc(size_t size);
void	 osdep_free(void *p, size_t size);
/*
 * Allocate a zeroed DMA common buffer of 'size' bytes whose device address
 * is aligned to 'align' bytes (0 = page alignment). Returns 0 or ENOMEM.
 */
int	 osdep_dma_alloc(void *os, struct iwx_dma_info *dma, size_t size,
	    size_t align);
void	 osdep_dma_free(void *os, struct iwx_dma_info *dma);

/* ---- synchronization ------------------------------------------------- */
/* Device spinlock shared by the PASSIVE command path and the DPC. */
void	 osdep_lock(void *os);
void	 osdep_unlock(void *os);
/* Auto-reset wait channels. Wait returns 0 if signalled, else ETIMEDOUT. */
void	 osdep_event_signal(void *os, enum osdep_event_id ev);
void	 osdep_event_clear(void *os, enum osdep_event_id ev);
int	 osdep_event_wait(void *os, enum osdep_event_id ev, uint32_t timeout_ms);

/* ---- logging ---------------------------------------------------------- */
void	 osdep_log(void *os, enum osdep_log_level lvl, const char *fmt, ...);

/* ---- firmware file ----------------------------------------------------- */
/* Load a whole file (PASSIVE). Buffer stays valid until osdep_fw_release. */
int	 osdep_fw_load(void *os, const char *name, uint8_t **data, size_t *len);
void	 osdep_fw_release(void *os, uint8_t *data, size_t len);

/* ---- upcalls from the core (may run in the DPC) ------------------------ */
/* Deliver one received frame; the backend must copy 'frame' before return. */
void	 osdep_rx_frame(void *os, const struct iwx_rx_meta *meta,
	    const uint8_t *frame, uint32_t len);
/* Fatal device condition; backend schedules stop/dump at PASSIVE_LEVEL. */
void	 osdep_fatal(void *os, enum osdep_fatal_reason reason);

#ifdef __cplusplus
}
#endif

#endif /* AIRDROP_OSDEP_H */
