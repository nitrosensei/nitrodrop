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
 * Bookkeeping behind the IOCTL queues, free of WDF types so the host tests
 * (driver/tests/test_queues.c) exercise exactly the code the driver runs.
 * The caller provides the locking (RxLock / TxLock in airdrop.h).
 *
 * RX ring: fixed number of slots; when full, the NEW frame is dropped and
 * counted, and the next frame that fits reports how many were lost
 * (DroppedSinceLast). Oldest frames are handed out first.
 *
 * TX slots: one per pending INJECT_FRAME that wants a result. The cookie
 * given to iwx_tx_inject() is slot index | generation << 8, so a late
 * status for a recycled slot is ignored. A slot whose request was
 * cancelled stays allocated until the firmware status (or a flush)
 * arrives, then is freed without completing anything.
 */
#ifndef AIRDROP_QUEUES_H
#define AIRDROP_QUEUES_H

#define AQ_RX_SLOTS		128
#define AQ_TX_SLOTS		64	/* == IWX_TX_MAX_INFLIGHT */

/* ---- RX ring ------------------------------------------------------------ */
typedef struct _AQ_RX_RING {
	unsigned int Head;		/* next slot to fill */
	unsigned int Count;		/* filled slots */
	unsigned int DroppedPending;	/* lost since the last stored frame */
	unsigned int DroppedTotal;
} AQ_RX_RING;

/* Reserve the slot for a new frame. Returns 0 (and counts) when full. */
static __inline int
AqRxReserve(AQ_RX_RING *r, unsigned int *Slot, unsigned int *DroppedSinceLast)
{
	if (r->Count >= AQ_RX_SLOTS) {
		r->DroppedPending++;
		r->DroppedTotal++;
		return 0;
	}
	*Slot = r->Head;
	*DroppedSinceLast = r->DroppedPending;
	r->DroppedPending = 0;
	r->Head = (r->Head + 1) % AQ_RX_SLOTS;
	r->Count++;
	return 1;
}

/* Count a frame dropped before reaching the ring (e.g. no ring yet). */
static __inline void
AqRxDrop(AQ_RX_RING *r)
{
	r->DroppedPending++;
	r->DroppedTotal++;
}

/* Oldest filled slot; 0 when empty. */
static __inline int
AqRxPeek(const AQ_RX_RING *r, unsigned int *Slot)
{
	if (r->Count == 0)
		return 0;
	*Slot = (r->Head + AQ_RX_SLOTS - r->Count) % AQ_RX_SLOTS;
	return 1;
}

static __inline void
AqRxConsume(AQ_RX_RING *r)
{
	if (r->Count > 0)
		r->Count--;
}

static __inline void
AqRxReset(AQ_RX_RING *r)
{
	r->Head = 0;
	r->Count = 0;
	r->DroppedPending = 0;
}

/* ---- TX slots ----------------------------------------------------------- */
#define AQ_TX_FREE	0
#define AQ_TX_PENDING	1	/* submitted, waiting for the status */
#define AQ_TX_DONE	2	/* status stored, request not completed yet */

typedef struct _AQ_TX_RESULT {
	unsigned int	Status;		/* AIRDROP_TXST_* == IWX_TXST_* */
	unsigned int	FwStatus;
	unsigned char	Retries;
	unsigned char	RtsRetries;
	unsigned short	Sequence;
	unsigned int	Rate;
	unsigned int	Flags;
} AQ_TX_RESULT;

typedef struct _AQ_TX_SLOT {
	unsigned char	State;
	unsigned char	Gen;		/* 1..255, never 0 */
	void		*Request;	/* WDFREQUEST, NULL once cancelled */
	AQ_TX_RESULT	Result;
} AQ_TX_SLOT;

typedef struct _AQ_TX_TABLE {
	AQ_TX_SLOT	Slot[AQ_TX_SLOTS];
	unsigned int	InUse;
} AQ_TX_TABLE;

#define AQ_TX_COOKIE(idx, gen)	((unsigned int)(idx) | ((unsigned int)(gen) << 8))

/* Allocate a slot for Request. Returns the cookie, or 0xffffffff. */
static __inline unsigned int
AqTxAlloc(AQ_TX_TABLE *t, void *Request)
{
	unsigned int i;

	for (i = 0; i < AQ_TX_SLOTS; i++) {
		AQ_TX_SLOT *s = &t->Slot[i];

		if (s->State != AQ_TX_FREE)
			continue;
		s->Gen = (unsigned char)(s->Gen + 1);
		if (s->Gen == 0)
			s->Gen = 1;
		s->State = AQ_TX_PENDING;
		s->Request = Request;
		t->InUse++;
		return AQ_TX_COOKIE(i, s->Gen);
	}
	return 0xffffffffu;
}

static __inline AQ_TX_SLOT *
AqTxLookup(AQ_TX_TABLE *t, unsigned int Cookie)
{
	unsigned int i = Cookie & 0xff;

	if (i >= AQ_TX_SLOTS || (Cookie >> 16) != 0 ||
	    t->Slot[i].State == AQ_TX_FREE ||
	    t->Slot[i].Gen != ((Cookie >> 8) & 0xff))
		return (AQ_TX_SLOT *)0;
	return &t->Slot[i];
}

static __inline void
AqTxFree(AQ_TX_TABLE *t, AQ_TX_SLOT *s)
{
	if (s->State != AQ_TX_FREE) {
		s->State = AQ_TX_FREE;
		s->Request = (void *)0;
		t->InUse--;
	}
}

/* Firmware status arrived. Returns 1 if a request now waits for it. */
static __inline int
AqTxComplete(AQ_TX_TABLE *t, unsigned int Cookie, const AQ_TX_RESULT *Res)
{
	AQ_TX_SLOT *s = AqTxLookup(t, Cookie);

	if (s == (AQ_TX_SLOT *)0 || s->State != AQ_TX_PENDING)
		return 0;
	if (s->Request == (void *)0) {	/* cancelled meanwhile */
		AqTxFree(t, s);
		return 0;
	}
	s->Result = *Res;
	s->State = AQ_TX_DONE;
	return 1;
}

/* Next finished slot: hands out its request + result and frees it. */
static __inline int
AqTxTakeDone(AQ_TX_TABLE *t, void **Request, AQ_TX_RESULT *Res)
{
	unsigned int i;

	for (i = 0; i < AQ_TX_SLOTS; i++) {
		AQ_TX_SLOT *s = &t->Slot[i];

		if (s->State != AQ_TX_DONE)
			continue;
		*Request = s->Request;
		*Res = s->Result;
		AqTxFree(t, s);
		return 1;
	}
	return 0;
}

/* Request cancelled: forget it (the slot waits for its status). */
static __inline int
AqTxForgetRequest(AQ_TX_TABLE *t, void *Request)
{
	unsigned int i;

	for (i = 0; i < AQ_TX_SLOTS; i++) {
		AQ_TX_SLOT *s = &t->Slot[i];

		if (s->State == AQ_TX_FREE || s->Request != Request)
			continue;
		s->Request = (void *)0;
		if (s->State == AQ_TX_DONE)
			AqTxFree(t, s);
		return 1;
	}
	return 0;
}

/* Submission failed before the hardware saw the frame. */
static __inline void *
AqTxAbort(AQ_TX_TABLE *t, unsigned int Cookie)
{
	AQ_TX_SLOT *s = AqTxLookup(t, Cookie);
	void *req;

	if (s == (AQ_TX_SLOT *)0)
		return (void *)0;
	req = s->Request;
	AqTxFree(t, s);
	return req;
}

/* Device gone: take every request still attached, free all slots. */
static __inline int
AqTxTakeAny(AQ_TX_TABLE *t, void **Request)
{
	unsigned int i;

	for (i = 0; i < AQ_TX_SLOTS; i++) {
		AQ_TX_SLOT *s = &t->Slot[i];

		if (s->State == AQ_TX_FREE)
			continue;
		*Request = s->Request;
		AqTxFree(t, s);
		if (*Request != (void *)0)
			return 1;
	}
	return 0;
}

#endif /* AIRDROP_QUEUES_H */
