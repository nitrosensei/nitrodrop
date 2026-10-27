"""Faz 0 / BLE testi.

1) Windows, Apple (0x004C) üretici verisiyle AirDrop tipi (0x05) BLE duyurusu yayınlayabiliyor mu?
2) Çevredeki Apple BLE duyurularını görebiliyor muyuz? (iPhone yakındaysa)

AirDrop duyuru biçimi (OpenDrop'tan): 05 12 | 8x00 | 01 | 4 x 2 bayt kişi hash'i | 00
Hash'ler sıfır = "herkes" modu için yeterli.
"""
import asyncio
import sys

from winrt.windows.devices.bluetooth.advertisement import (
    BluetoothLEAdvertisementPublisher,
    BluetoothLEAdvertisementWatcher,
    BluetoothLEManufacturerData,
    BluetoothLEScanningMode,
)
from winrt.windows.storage.streams import DataWriter

APPLE = 0x004C
AIRDROP_PAYLOAD = bytes([0x05, 0x12]) + bytes(8) + bytes([0x01]) + bytes(8) + bytes([0x00])

STATUS = {0: "Created", 1: "Waiting", 2: "Started", 3: "Stopping", 4: "Stopped", 5: "Aborted"}


def buf(b: bytes):
    w = DataWriter()
    w.write_bytes(b)
    return w.detach_buffer()


async def publish_test(seconds: float = 5.0) -> bool:
    pub = BluetoothLEAdvertisementPublisher()
    md = BluetoothLEManufacturerData()
    md.company_id = APPLE
    md.data = buf(AIRDROP_PAYLOAD)
    pub.advertisement.manufacturer_data.append(md)

    errors = []
    pub.add_status_changed(lambda s, e: errors.append((STATUS.get(int(e.status)), int(e.error))))
    try:
        pub.start()
    except Exception as ex:  # Windows kısıtlı veri tipinde burada hata fırlatır
        print(f"[YAYIN] start() hata: {ex!r}")
        return False
    await asyncio.sleep(seconds)
    st = STATUS.get(int(pub.status))
    pub.stop()
    print(f"[YAYIN] durum={st} olaylar={errors}")
    return st == "Started" or any(s == "Started" for s, _ in errors)


async def scan_test(seconds: float = 10.0) -> int:
    seen = {}
    w = BluetoothLEAdvertisementWatcher()
    w.scanning_mode = BluetoothLEScanningMode.ACTIVE

    def on_recv(_s, e):
        for md in e.advertisement.manufacturer_data:
            if md.company_id != APPLE:
                continue
            raw = bytes(memoryview(md.data)) if hasattr(md.data, "__buffer__") else None
            if raw is None:
                from winrt.windows.storage.streams import DataReader
                r = DataReader.from_buffer(md.data)
                arr = bytearray(md.data.length)
                r.read_bytes(arr)
                raw = bytes(arr)
            addr = f"{e.bluetooth_address:012X}"
            if addr not in seen:
                seen[addr] = (e.raw_signal_strength_in_dbm, raw.hex())

    w.add_received(on_recv)
    w.start()
    await asyncio.sleep(seconds)
    w.stop()
    types = {}
    for addr, (rssi, hx) in seen.items():
        t = hx[:2]
        types[t] = types.get(t, 0) + 1
    print(f"[TARAMA] {len(seen)} Apple cihaz adresi; tip dağılımı (ilk bayt): {types}")
    for addr, (rssi, hx) in list(seen.items())[:8]:
        print(f"   {addr} rssi={rssi} {hx[:40]}")
    return len(seen)


async def main():
    ok = await publish_test()
    n = await scan_test()
    print(f"SONUC yayin={'OK' if ok else 'BASARISIZ'} tarama={n}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
