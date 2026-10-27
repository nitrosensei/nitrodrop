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

/* IOCTL queue bookkeeping (driver/kmdf/airdrop_queues.h). */

#include "mock.h"
#include "../kmdf/airdrop_queues.h"

static void
test_rx_ring_queue(void)
{
	AQ_RX_RING r;
	unsigned int slot, dropped, i, ok;

	memset(&r, 0, sizeof(r));
	CHECK_EQ(AqRxPeek(&r, &slot), 0);

	/* fill completely: slots 0..127 in order, nothing dropped */
	ok = 1;
	for (i = 0; i < AQ_RX_SLOTS; i++)
		if (!AqRxReserve(&r, &slot, &dropped) || slot != i ||
		    dropped != 0)
			ok = 0;
	CHECK(ok);
	CHECK_EQ(r.Count, AQ_RX_SLOTS);

	/* overflow: new frames dropped and counted */
	for (i = 0; i < 5; i++)
		CHECK_EQ(AqRxReserve(&r, &slot, &dropped), 0);
	AqRxDrop(&r);
	CHECK_EQ(r.DroppedPending, 6);
	CHECK_EQ(r.DroppedTotal, 6);

	/* oldest first */
	CHECK_EQ(AqRxPeek(&r, &slot), 1);
	CHECK_EQ(slot, 0);
	AqRxConsume(&r);
	CHECK_EQ(AqRxPeek(&r, &slot), 1);
	CHECK_EQ(slot, 1);
	AqRxConsume(&r);

	/* next stored frame reports the loss, wraps into slot 0 */
	CHECK_EQ(AqRxReserve(&r, &slot, &dropped), 1);
	CHECK_EQ(slot, 0);
	CHECK_EQ(dropped, 6);
	CHECK_EQ(AqRxReserve(&r, &slot, &dropped), 1);
	CHECK_EQ(slot, 1);
	CHECK_EQ(dropped, 0);

	/* drain in FIFO order across the wrap */
	ok = 1;
	for (i = 2; i < AQ_RX_SLOTS + 2; i++) {
		if (!AqRxPeek(&r, &slot) || slot != i % AQ_RX_SLOTS)
			ok = 0;
		AqRxConsume(&r);
	}
	CHECK(ok);
	CHECK_EQ(AqRxPeek(&r, &slot), 0);
	AqRxConsume(&r);		/* harmless on empty */
	CHECK_EQ(r.Count, 0);

	AqRxReset(&r);
	CHECK_EQ(r.Head, 0);
	CHECK_EQ(r.DroppedTotal, 6);	/* lifetime counter kept */
}

static void
test_tx_slots(void)
{
	static AQ_TX_TABLE t;
	AQ_TX_RESULT res, out;
	unsigned int c[AQ_TX_SLOTS + 1], i, ok, old;
	void *req;
	int reqs[AQ_TX_SLOTS];

	memset(&t, 0, sizeof(t));
	memset(&res, 0, sizeof(res));

	ok = 1;
	for (i = 0; i < AQ_TX_SLOTS; i++) {
		c[i] = AqTxAlloc(&t, &reqs[i]);
		if (c[i] == 0xffffffffu || (c[i] & 0xff) != i ||
		    ((c[i] >> 8) & 0xff) == 0)
			ok = 0;
	}
	CHECK(ok);
	CHECK_EQ(AqTxAlloc(&t, &reqs[0]), 0xffffffffu);	/* full */
	CHECK_EQ(t.InUse, AQ_TX_SLOTS);

	/* status for slot 3 */
	res.Status = 1;
	res.Retries = 7;
	CHECK_EQ(AqTxComplete(&t, c[3], &res), 1);
	CHECK_EQ(AqTxComplete(&t, c[3], &res), 0);	/* duplicate */
	CHECK_EQ(AqTxTakeDone(&t, &req, &out), 1);
	CHECK(req == &reqs[3]);
	CHECK_EQ(out.Retries, 7);
	CHECK_EQ(AqTxTakeDone(&t, &req, &out), 0);
	CHECK_EQ(t.InUse, AQ_TX_SLOTS - 1);

	/* recycled slot gets a new generation; old cookie ignored */
	old = c[3];
	c[3] = AqTxAlloc(&t, &reqs[3]);
	CHECK_EQ(c[3] & 0xff, 3);
	CHECK(c[3] != old);
	CHECK_EQ(AqTxComplete(&t, old, &res), 0);
	CHECK(AqTxLookup(&t, old) == NULL);
	CHECK(AqTxLookup(&t, c[3]) != NULL);
	CHECK(AqTxLookup(&t, 0xffffffffu) == NULL);	/* COOKIE_NONE */
	CHECK(AqTxLookup(&t, 200) == NULL);

	/* cancel before the status: slot absorbs the late status */
	CHECK_EQ(AqTxForgetRequest(&t, &reqs[5]), 1);
	CHECK_EQ(t.InUse, AQ_TX_SLOTS);
	CHECK_EQ(AqTxComplete(&t, c[5], &res), 0);
	CHECK_EQ(t.InUse, AQ_TX_SLOTS - 1);
	CHECK_EQ(AqTxTakeDone(&t, &req, &out), 0);

	/* cancel after the status, before completion */
	CHECK_EQ(AqTxComplete(&t, c[6], &res), 1);
	CHECK_EQ(AqTxForgetRequest(&t, &reqs[6]), 1);
	CHECK_EQ(AqTxTakeDone(&t, &req, &out), 0);
	CHECK_EQ(t.InUse, AQ_TX_SLOTS - 2);
	CHECK_EQ(AqTxForgetRequest(&t, &reqs[6]), 0);

	/* submission failure */
	CHECK(AqTxAbort(&t, c[7]) == &reqs[7]);
	CHECK(AqTxAbort(&t, c[7]) == NULL);
	CHECK_EQ(t.InUse, AQ_TX_SLOTS - 3);

	/* generation never 0 across wraps */
	ok = 1;
	for (i = 0; i < 600; i++) {
		unsigned int k = AqTxAlloc(&t, &reqs[0]);
		if (k == 0xffffffffu || ((k >> 8) & 0xff) == 0)
			ok = 0;
		if (AqTxAbort(&t, k) != &reqs[0])
			ok = 0;
	}
	CHECK(ok);

	/* removal: every attached request handed out once */
	i = 0;
	while (AqTxTakeAny(&t, &req))
		i++;
	CHECK_EQ(i, AQ_TX_SLOTS - 3);
	CHECK_EQ(t.InUse, 0);
}

void
test_queues(void)
{
	printf("test_queues\n");
	test_rx_ring_queue();
	test_tx_slots();
}
