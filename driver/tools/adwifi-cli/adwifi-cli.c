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
 * adwifi-cli - user-mode test client for the airdrop-win Wi-Fi driver.
 *
 *   adwifi-cli info
 *   adwifi-cli start [-c CH] [-b BSSID] [-i TU] [--listener] [--keep-bad-fcs]
 *                    [--no-promisc]
 *   adwifi-cli read  [-n COUNT] [-t SECONDS] [-w FILE.pcap] [-q]
 *   adwifi-cli inject -r RATE [--ht] [--sgi] [--noack] [--seq] [--hipri]
 *                    [-n COUNT] [--nowait] (-x HEXBYTES | -f FILE)
 *   adwifi-cli stop
 *
 * The device is exclusive and the radio is switched off when the handle
 * closes, so "start" alone only proves bring-up; "run" keeps one handle:
 *   adwifi-cli run [start options] -- read|inject [options]
 *
 * Build (VS Build Tools prompt):
 *   cl /nologo /W4 /WX /O2 adwifi-cli.c /link cfgmgr32.lib
 *
 * Needs Administrator (device ACL) and the driver installed.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>
#include <cfgmgr32.h>
#include <initguid.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../../include/airdrop_ioctl.h"

#define MAX_OUTSTANDING	8

static void
usage(void)
{
	fprintf(stderr,
	    "usage:\n"
	    "  adwifi-cli info\n"
	    "  adwifi-cli start [-c CH] [-b BSSID] [-i TU] [--listener]\n"
	    "                   [--keep-bad-fcs] [--no-promisc]\n"
	    "  adwifi-cli read  [-n COUNT] [-t SECONDS] [-w FILE.pcap] [-q]\n"
	    "  adwifi-cli inject -r RATE [--ht] [--sgi] [--noack] [--seq]\n"
	    "                   [--hipri] [-n COUNT] [--nowait]\n"
	    "                   (-x HEXBYTES | -f FILE)\n"
	    "  adwifi-cli stop\n"
	    "  adwifi-cli run [start options] -- read|inject [options]\n"
	    "RATE: legacy in 500 kb/s units (2 4 11 22 12 18 24 36 48 72 96 108),\n"
	    "      with --ht an HT MCS index 0-15.\n"
	    "Default START: channel 6, IBSS BSSID 00:25:00:ff:94:73 (Owly).\n");
	exit(2);
}

/* ---- device ------------------------------------------------------------- */

static HANDLE
open_device(void)
{
	WCHAR *list = NULL, *p;
	ULONG len = 0;
	CONFIGRET cr;
	HANDLE h = INVALID_HANDLE_VALUE;

	for (;;) {
		cr = CM_Get_Device_Interface_List_SizeW(&len,
		    (LPGUID)&GUID_DEVINTERFACE_AIRDROP_WIFI, NULL,
		    CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
		if (cr != CR_SUCCESS || len <= 1) {
			fprintf(stderr, "no airdrop-win device present "
			    "(driver installed? cr=%lu)\n", (unsigned long)cr);
			return INVALID_HANDLE_VALUE;
		}
		free(list);
		list = calloc(len, sizeof(WCHAR));
		if (list == NULL)
			return INVALID_HANDLE_VALUE;
		cr = CM_Get_Device_Interface_ListW(
		    (LPGUID)&GUID_DEVINTERFACE_AIRDROP_WIFI, NULL, list, len,
		    CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
		if (cr != CR_BUFFER_SMALL)
			break;
	}
	if (cr == CR_SUCCESS) {
		for (p = list; *p; p += wcslen(p) + 1) {
			h = CreateFileW(p, GENERIC_READ | GENERIC_WRITE, 0, NULL,
			    OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
			if (h != INVALID_HANDLE_VALUE)
				break;
			fprintf(stderr, "open %ls: error %lu%s\n", p,
			    GetLastError(), GetLastError() == ERROR_ACCESS_DENIED ?
			    " (run as Administrator; device is exclusive)" : "");
		}
	}
	free(list);
	return h;
}

/* synchronous DeviceIoControl on an overlapped handle */
static BOOL
ioctl_sync(HANDLE h, DWORD code, void *in, DWORD inlen, void *out,
    DWORD outlen, DWORD *ret)
{
	OVERLAPPED ov;
	DWORD n = 0;
	BOOL ok;

	memset(&ov, 0, sizeof(ov));
	ov.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
	if (ov.hEvent == NULL)
		return FALSE;
	ok = DeviceIoControl(h, code, in, inlen, out, outlen, NULL, &ov);
	if (!ok && GetLastError() == ERROR_IO_PENDING)
		ok = GetOverlappedResult(h, &ov, &n, TRUE);
	else if (ok)
		ok = GetOverlappedResult(h, &ov, &n, FALSE);
	CloseHandle(ov.hEvent);
	if (ret != NULL)
		*ret = n;
	return ok;
}

/* ---- info --------------------------------------------------------------- */

static const char *
state_name(UINT32 s)
{
	switch (s) {
	case AIRDROP_STATE_DOWN:	return "DOWN";
	case AIRDROP_STATE_FW_READY:	return "FW_READY";
	case AIRDROP_STATE_RADIO_ON:	return "RADIO_ON";
	case AIRDROP_STATE_ERROR:	return "ERROR";
	default:			return "?";
	}
}

static int
cmd_info(HANDLE h)
{
	AIRDROP_INFO in;
	DWORD n;
	UINT32 i;

	memset(&in, 0, sizeof(in));
	if (!ioctl_sync(h, IOCTL_AIRDROP_GET_INFO, NULL, 0, &in, sizeof(in),
	    &n)) {
		fprintf(stderr, "GET_INFO failed: %lu\n", GetLastError());
		return 1;
	}
	printf("ABI           %u (%lu bytes)\n", in.AbiVersion, (unsigned long)n);
	printf("state         %s\n", state_name(in.State));
	printf("init status   0x%08x\n", (unsigned)in.InitStatus);
	printf("MAC           %02x:%02x:%02x:%02x:%02x:%02x\n",
	    in.MacAddress[0], in.MacAddress[1], in.MacAddress[2],
	    in.MacAddress[3], in.MacAddress[4], in.MacAddress[5]);
	printf("firmware      %s\n", in.FirmwareVersion);
	printf("hw rev / rf   0x%08x / 0x%08x\n", in.HwRev, in.HwRfId);
	printf("rfkill / msix %u / %u\n", in.RfKill, in.Msix);
	printf("last fw error 0x%08x\n", in.LastFirmwareError);
	printf("rx            %u frames, %u dropped, %u bad FCS\n",
	    in.RxFrames, in.RxDropped,
	    n >= sizeof(in) ? in.RxBadFcs : 0);
	if (n >= sizeof(in)) {
		printf("tx            %u submitted, %u ok, %u failed, "
		    "%u flushed, %u pending\n", in.TxSubmitted, in.TxSuccess,
		    in.TxFailed, in.TxFlushed, in.TxPending);
		printf("radio         channel %u, mode %s\n", in.RadioChannel,
		    in.RadioMode == AIRDROP_RADIO_MODE_LISTENER ? "listener" :
		    in.RadioMode == AIRDROP_RADIO_MODE_IBSS ? "ibss" : "off");
	}
	printf("channels      ");
	for (i = 0; i < in.ChannelCount && i < AIRDROP_MAX_CHANNELS; i++)
		printf("%u ", in.Channels[i]);
	printf("\n");
	return 0;
}

/* ---- start -------------------------------------------------------------- */

static int
parse_mac(const char *s, UINT8 *mac)
{
	unsigned int b[6];
	int i;

	if (sscanf_s(s, "%x:%x:%x:%x:%x:%x", &b[0], &b[1], &b[2], &b[3],
	    &b[4], &b[5]) != 6)
		return 0;
	for (i = 0; i < 6; i++) {
		if (b[i] > 0xff)
			return 0;
		mac[i] = (UINT8)b[i];
	}
	return 1;
}

/* parses start options from argv[*i]; stops at "--" or end */
static void
parse_start(int argc, char **argv, int *i, AIRDROP_START_PARAMS *p)
{
	static const UINT8 owly[6] = { 0x00, 0x25, 0x00, 0xff, 0x94, 0x73 };

	memset(p, 0, sizeof(*p));
	p->Size = sizeof(*p);
	p->Channel = 6;
	memcpy(p->Bssid, owly, 6);
	for (; *i < argc && strcmp(argv[*i], "--") != 0; (*i)++) {
		const char *a = argv[*i];

		if (!strcmp(a, "-c") && *i + 1 < argc)
			p->Channel = (UINT8)atoi(argv[++*i]);
		else if (!strcmp(a, "-b") && *i + 1 < argc) {
			if (!parse_mac(argv[++*i], p->Bssid))
				usage();
		} else if (!strcmp(a, "-i") && *i + 1 < argc)
			p->BeaconIntervalTu = (UINT16)atoi(argv[++*i]);
		else if (!strcmp(a, "--listener"))
			p->Flags |= AIRDROP_START_F_LISTENER_ONLY;
		else if (!strcmp(a, "--keep-bad-fcs"))
			p->Flags |= AIRDROP_START_F_KEEP_BAD_FCS;
		else if (!strcmp(a, "--no-promisc"))
			p->Flags |= AIRDROP_START_F_NO_PROMISC;
		else
			usage();
	}
}

static int
do_start(HANDLE h, AIRDROP_START_PARAMS *p)
{
	if (!ioctl_sync(h, IOCTL_AIRDROP_START, p, sizeof(*p), NULL, 0,
	    NULL)) {
		fprintf(stderr, "START failed: %lu\n", GetLastError());
		return 1;
	}
	printf("radio on: channel %u, %s\n", p->Channel,
	    (p->Flags & AIRDROP_START_F_LISTENER_ONLY) ? "listener" : "ibss");
	return 0;
}

/* ---- read --------------------------------------------------------------- */

static FILE *pcap;

static void
pcap_open(const char *path)
{
	struct {
		UINT32 magic;
		UINT16 vmaj, vmin;
		INT32 zone;
		UINT32 sigfigs, snaplen, linktype;
	} gh = { 0xa1b2c3d4, 2, 4, 0, 0, 65535, 127 /* RADIOTAP */ };

	if (fopen_s(&pcap, path, "wb") != 0 || pcap == NULL) {
		fprintf(stderr, "cannot create %s\n", path);
		exit(1);
	}
	fwrite(&gh, sizeof(gh), 1, pcap);
}

static UINT16
chan_freq(UINT8 ch)
{
	if (ch == 14)
		return 2484;
	if (ch < 14)
		return (UINT16)(2407 + 5 * ch);
	return (UINT16)(5000 + 5 * ch);
}

/* radiotap: TSFT, Flags, Rate, Channel, dBm signal, MCS */
static void
pcap_write(const AIRDROP_RX_HEADER *h, const UINT8 *frame)
{
	UINT8 rt[64];
	UINT32 present = 0, off = 8, rec[4];
	UINT8 flags = 0;
	UINT64 ts;

	memset(rt, 0, sizeof(rt));
	if (h->Flags & AIRDROP_RX_F_TSF_VALID) {
		present |= 1u << 0;
		memcpy(rt + off, &h->Tsf, 8);
		off += 8;
	}
	present |= 1u << 1;
	if (h->Flags & AIRDROP_RX_F_SHORT_PREAMBLE)
		flags |= 0x02;
	if (h->Flags & AIRDROP_RX_F_BAD_FCS)
		flags |= 0x40;
	if (h->Flags & AIRDROP_RX_F_SGI)
		flags |= 0x80;
	rt[off++] = flags;
	if (h->Encoding <= AIRDROP_RX_ENC_OFDM) {
		present |= 1u << 2;
		rt[off++] = (UINT8)h->LegacyRate;
	}
	off = (off + 1) & ~1u;
	present |= 1u << 3;
	{
		UINT16 f = chan_freq(h->Channel);
		UINT16 cf = (UINT16)(h->Channel <= 14 ? 0x0080 : 0x0100);

		cf |= (UINT16)(h->Encoding == AIRDROP_RX_ENC_CCK ? 0x0020 : 0x0040);
		memcpy(rt + off, &f, 2);
		memcpy(rt + off + 2, &cf, 2);
		off += 4;
	}
	present |= 1u << 5;
	rt[off++] = (UINT8)h->RssiDbm;
	if (h->Encoding == AIRDROP_RX_ENC_HT) {
		present |= 1u << 19;
		rt[off++] = 0x07;	/* known: bw, mcs, gi */
		rt[off++] = (UINT8)((h->Bandwidth == 1 ? 1 : 0) |
		    ((h->Flags & AIRDROP_RX_F_SGI) ? 0x04 : 0));
		rt[off++] = h->Mcs;
	}
	rt[2] = (UINT8)off;
	memcpy(rt + 4, &present, 4);

	ts = h->HostTimestamp / 10;	/* 100 ns -> us (interrupt time) */
	rec[0] = (UINT32)(ts / 1000000);
	rec[1] = (UINT32)(ts % 1000000);
	rec[2] = off + h->FrameLength;
	rec[3] = off + h->OriginalLength;
	fwrite(rec, sizeof(rec), 1, pcap);
	fwrite(rt, off, 1, pcap);
	fwrite(frame, h->FrameLength, 1, pcap);
}

static void
print_frame(const AIRDROP_RX_HEADER *h, const UINT8 *f)
{
	static const char *enc[] = { "CCK", "OFDM", "HT", "VHT", "HE", "EHT" };

	printf("ch%-3u %4d dBm %-4s ", h->Channel, h->RssiDbm,
	    h->Encoding < 6 ? enc[h->Encoding] : "?");
	if (h->Encoding <= AIRDROP_RX_ENC_OFDM)
		printf("%5.1fM ", h->LegacyRate / 2.0);
	else
		printf("MCS%-2u ", h->Mcs);
	printf("len %4u fc %02x%02x", h->OriginalLength, f[0],
	    h->FrameLength > 1 ? f[1] : 0);
	if (h->FrameLength >= 16)
		printf(" a2 %02x:%02x:%02x:%02x:%02x:%02x", f[10], f[11], f[12],
		    f[13], f[14], f[15]);
	if (h->Flags & AIRDROP_RX_F_BAD_FCS)
		printf(" BADFCS");
	if (h->DroppedSinceLast)
		printf(" (dropped %u)", h->DroppedSinceLast);
	printf("\n");
}

static int
cmd_read(HANDLE h, int argc, char **argv, int i)
{
	OVERLAPPED ov[MAX_OUTSTANDING];
	UINT8 *buf[MAX_OUTSTANDING];
	HANDLE ev[MAX_OUTSTANDING];
	long count = -1, got = 0;
	DWORD secs = 0, start = GetTickCount(), n;
	int quiet = 0, k;

	for (; i < argc; i++) {
		if (!strcmp(argv[i], "-n") && i + 1 < argc)
			count = atol(argv[++i]);
		else if (!strcmp(argv[i], "-t") && i + 1 < argc)
			secs = (DWORD)atol(argv[++i]);
		else if (!strcmp(argv[i], "-w") && i + 1 < argc)
			pcap_open(argv[++i]);
		else if (!strcmp(argv[i], "-q"))
			quiet = 1;
		else
			usage();
	}

	for (k = 0; k < MAX_OUTSTANDING; k++) {
		buf[k] = malloc(AIRDROP_RX_BUFFER_SIZE);
		ev[k] = CreateEventW(NULL, TRUE, FALSE, NULL);
		if (buf[k] == NULL || ev[k] == NULL)
			return 1;
		memset(&ov[k], 0, sizeof(ov[k]));
		ov[k].hEvent = ev[k];
		if (!DeviceIoControl(h, IOCTL_AIRDROP_READ_FRAME, NULL, 0, buf[k],
		    (DWORD)AIRDROP_RX_BUFFER_SIZE, NULL, &ov[k]) &&
		    GetLastError() != ERROR_IO_PENDING) {
			fprintf(stderr, "READ_FRAME failed: %lu\n",
			    GetLastError());
			return 1;
		}
	}

	while (count < 0 || got < count) {
		DWORD w, timeout = INFINITE;

		if (secs) {
			DWORD el = GetTickCount() - start;
			if (el >= secs * 1000)
				break;
			timeout = secs * 1000 - el;
		}
		w = WaitForMultipleObjects(MAX_OUTSTANDING, ev, FALSE, timeout);
		if (w == WAIT_TIMEOUT)
			break;
		if (w >= WAIT_OBJECT_0 + MAX_OUTSTANDING)
			return 1;
		k = (int)(w - WAIT_OBJECT_0);
		if (!GetOverlappedResult(h, &ov[k], &n, FALSE)) {
			fprintf(stderr, "READ_FRAME completed with %lu\n",
			    GetLastError());
			break;
		} else {
			const AIRDROP_RX_HEADER *rh = (void *)buf[k];
			const UINT8 *f = buf[k] + rh->HeaderSize;

			got++;
			if (!quiet)
				print_frame(rh, f);
			if (pcap)
				pcap_write(rh, f);
		}
		ResetEvent(ev[k]);
		memset(&ov[k], 0, sizeof(ov[k]));
		ov[k].hEvent = ev[k];
		if (!DeviceIoControl(h, IOCTL_AIRDROP_READ_FRAME, NULL, 0, buf[k],
		    (DWORD)AIRDROP_RX_BUFFER_SIZE, NULL, &ov[k]) &&
		    GetLastError() != ERROR_IO_PENDING)
			break;
	}
	CancelIo(h);
	for (k = 0; k < MAX_OUTSTANDING; k++) {
		GetOverlappedResult(h, &ov[k], &n, TRUE);
		CloseHandle(ev[k]);
		free(buf[k]);
	}
	if (pcap)
		fclose(pcap);
	printf("%ld frames\n", got);
	return 0;
}

/* ---- inject ------------------------------------------------------------- */

static int
hexval(int c)
{
	if (c >= '0' && c <= '9')
		return c - '0';
	if (c >= 'a' && c <= 'f')
		return c - 'a' + 10;
	if (c >= 'A' && c <= 'F')
		return c - 'A' + 10;
	return -1;
}

static UINT32
parse_hex(const char *s, UINT8 *out, UINT32 max)
{
	UINT32 n = 0;
	int hi = -1, v;

	for (; *s; s++) {
		if ((v = hexval(*s)) < 0)
			continue;	/* separators */
		if (hi < 0)
			hi = v;
		else {
			if (n >= max)
				return 0;
			out[n++] = (UINT8)(hi << 4 | v);
			hi = -1;
		}
	}
	return hi < 0 ? n : 0;
}

static const char *
txst_name(UINT32 s)
{
	switch (s) {
	case AIRDROP_TXST_SUCCESS:	return "success";
	case AIRDROP_TXST_FAILED:	return "failed";
	case AIRDROP_TXST_FLUSHED:	return "flushed";
	case AIRDROP_TXST_NO_STATUS:	return "no-status";
	default:			return "?";
	}
}

static int
cmd_inject(HANDLE h, int argc, char **argv, int i)
{
	UINT8 req[sizeof(AIRDROP_TX_HEADER) + AIRDROP_TX_MAX_FRAME];
	AIRDROP_TX_HEADER *th = (void *)req;
	UINT8 *frame = req + sizeof(*th);
	AIRDROP_TX_RESULT res;
	UINT32 len = 0, rate = 0, flags = 0;
	long count = 1, k, ok = 0;
	int wait = 1, haverate = 0;
	DWORD n;

	for (; i < argc; i++) {
		const char *a = argv[i];

		if (!strcmp(a, "-r") && i + 1 < argc) {
			rate = (UINT32)atoi(argv[++i]);
			haverate = 1;
		} else if (!strcmp(a, "--ht"))
			flags |= AIRDROP_TX_F_RATE_HT;
		else if (!strcmp(a, "--sgi"))
			flags |= AIRDROP_TX_F_SGI;
		else if (!strcmp(a, "--noack"))
			flags |= AIRDROP_TX_F_NOACK;
		else if (!strcmp(a, "--seq"))
			flags |= AIRDROP_TX_F_ASSIGN_SEQ;
		else if (!strcmp(a, "--hipri"))
			flags |= AIRDROP_TX_F_HIGH_PRI;
		else if (!strcmp(a, "--nowait"))
			wait = 0;
		else if (!strcmp(a, "-n") && i + 1 < argc)
			count = atol(argv[++i]);
		else if (!strcmp(a, "-x") && i + 1 < argc)
			len = parse_hex(argv[++i], frame, AIRDROP_TX_MAX_FRAME);
		else if (!strcmp(a, "-f") && i + 1 < argc) {
			FILE *f;

			if (fopen_s(&f, argv[++i], "rb") != 0 || f == NULL) {
				fprintf(stderr, "cannot open %s\n", argv[i]);
				return 1;
			}
			len = (UINT32)fread(frame, 1, AIRDROP_TX_MAX_FRAME, f);
			fclose(f);
		} else
			usage();
	}
	if (!haverate || len < 10)
		usage();

	th->HeaderSize = sizeof(*th);
	th->FrameLength = len;
	th->Rate = rate;
	th->Flags = flags;
	for (k = 0; k < count; k++) {
		memset(&res, 0, sizeof(res));
		if (!ioctl_sync(h, IOCTL_AIRDROP_INJECT_FRAME, req,
		    (DWORD)(sizeof(*th) + len), wait ? &res : NULL,
		    wait ? (DWORD)sizeof(res) : 0, &n)) {
			fprintf(stderr, "INJECT_FRAME %ld failed: %lu\n", k,
			    GetLastError());
			return 1;
		}
		if (wait && n >= sizeof(res)) {
			printf("#%ld %s fw 0x%04x retries %u rts %u seq 0x%04x "
			    "rate 0x%08x flags 0x%x\n", k, txst_name(res.Status),
			    res.FwStatus, res.Retries, res.RtsRetries,
			    res.Sequence, res.InitialRate, res.Flags);
			if (res.Status == AIRDROP_TXST_SUCCESS)
				ok++;
		} else
			ok++;
	}
	printf("%ld/%ld %s\n", ok, count, wait ? "acknowledged/sent" : "queued");
	return 0;
}

/* ---- main --------------------------------------------------------------- */

int
main(int argc, char **argv)
{
	AIRDROP_START_PARAMS sp;
	HANDLE h;
	int rc = 0, i;

	if (argc < 2)
		usage();
	h = open_device();
	if (h == INVALID_HANDLE_VALUE)
		return 1;

	if (!strcmp(argv[1], "info"))
		rc = cmd_info(h);
	else if (!strcmp(argv[1], "start")) {
		i = 2;
		parse_start(argc, argv, &i, &sp);
		rc = do_start(h, &sp);
		if (rc == 0)
			printf("note: the radio stops when this handle closes; "
			    "use \"run\" to keep it up\n");
	} else if (!strcmp(argv[1], "stop"))
		rc = ioctl_sync(h, IOCTL_AIRDROP_STOP, NULL, 0, NULL, 0, NULL) ?
		    0 : 1;
	else if (!strcmp(argv[1], "read"))
		rc = cmd_read(h, argc, argv, 2);
	else if (!strcmp(argv[1], "inject"))
		rc = cmd_inject(h, argc, argv, 2);
	else if (!strcmp(argv[1], "run")) {
		i = 2;
		parse_start(argc, argv, &i, &sp);
		if (i + 1 >= argc)
			usage();
		rc = do_start(h, &sp);
		if (rc == 0) {
			i++;	/* skip "--" */
			if (!strcmp(argv[i], "read"))
				rc = cmd_read(h, argc, argv, i + 1);
			else if (!strcmp(argv[i], "inject"))
				rc = cmd_inject(h, argc, argv, i + 1);
			else
				usage();
			cmd_info(h);
		}
	} else
		usage();

	CloseHandle(h);
	return rc;
}
