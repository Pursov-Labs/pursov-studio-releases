import asyncio
import hashlib
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'runtime'))
from tg_probe import make_transport, probe_one, request_packet, validate_response
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


def response(nonce):
    body = struct.pack('<I', 0x05162463) + nonce + b's' * 16
    body += b'\x08' + b'q' * 8 + b'\0' * 3
    body += struct.pack('<IIQ', 0x1cb5c415, 1, 123)
    return b'\0' * 8 + struct.pack('<QI', 1, len(body)) + body


class FrameTests(unittest.TestCase):
    def test_worker_failure_exits_with_control_pipe_open(self):
        import json
        root = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)
            (source / 'proxy').mkdir()
            (source / 'proxy/__init__.py').write_text('')
            (source / 'proxy/config.py').write_text('def coerce_domain_list(x): return []\ndef parse_dc_ip_list(x): return {}\n')
            (source / 'proxy/tg_ws_proxy.py').write_text('import asyncio\nclass Config: pass\nproxy_config=Config()\n_server_instance=True\nasync def _run(stop):\n    await asyncio.sleep(.2)\n    raise RuntimeError("simulated")\n')
            process = subprocess.Popen([sys.executable, '-B', '-u', str(root / 'runtime/tg_runner.py')], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                cfg = {'host': '127.0.0.1', 'port': 1443, 'secret': 'a' * 32}
                process.stdin.write((json.dumps({'source': str(source), 'config': cfg}) + '\n').encode())
                process.stdin.flush()
                self.assertEqual(process.wait(timeout=4), 1)
                self.assertIn(b'"event": "error"', process.stdout.read())
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
                process.stdin.close()
                process.stdout.close()
                process.stderr.close()

    def test_response_nonce_and_structure(self):
        nonce = bytes(range(16))
        validate_response(response(nonce), nonce)
        with self.assertRaises(ValueError):
            validate_response(response(nonce), b'x' * 16)
        for frame in [b'', response(nonce)[:72], response(nonce)[:20] + b'\0' * 70]:
            with self.assertRaises(ValueError):
                validate_response(frame, nonce)

    def test_encrypted_header_matches_upstream_protocol(self):
        secret = b'a' * 16
        header, enc, dec = make_transport(secret, 4)
        key_iv = header[8:56]
        peer_dec = Cipher(algorithms.AES(hashlib.sha256(key_iv[:32] + secret).digest()), modes.CTR(key_iv[32:])).encryptor()
        plain = peer_dec.update(header)
        self.assertEqual(plain[56:60], b'\xee' * 4)
        self.assertEqual(struct.unpack_from('<h', plain, 60)[0], 4)
        payload = request_packet(bytes(range(16)))
        self.assertEqual(peer_dec.update(enc.update(payload)), payload)
        reverse = key_iv[::-1]
        peer_enc = Cipher(algorithms.AES(hashlib.sha256(reverse[:32] + secret).digest()), modes.CTR(reverse[32:])).encryptor()
        self.assertEqual(dec.update(peer_enc.update(payload)), payload)


class RoundTripTests(unittest.IsolatedAsyncioTestCase):
    async def run_case(self, wrong_nonce=False, stall=False):
        secret = bytes(range(16))

        async def client(reader, writer):
            try:
                header = await reader.readexactly(64)
                key_iv = header[8:56]
                dec = Cipher(algorithms.AES(hashlib.sha256(key_iv[:32] + secret).digest()), modes.CTR(key_iv[32:])).encryptor()
                dec.update(header)
                size = struct.unpack('<I', dec.update(await reader.readexactly(4)))[0]
                packet = dec.update(await reader.readexactly(size))
                nonce = packet[24:40]
                if stall:
                    await reader.read()
                    return
                packet = response(b'X' * 16 if wrong_nonce else nonce)
                reverse = key_iv[::-1]
                enc = Cipher(algorithms.AES(hashlib.sha256(reverse[:32] + secret).digest()), modes.CTR(reverse[32:])).encryptor()
                writer.write(enc.update(struct.pack('<I', len(packet)) + packet))
                await writer.drain()
            finally:
                writer.close()
                await writer.wait_closed()

        server = await asyncio.start_server(client, '127.0.0.1', 0)
        async with server:
            result = await probe_one('127.0.0.1', server.sockets[0].getsockname()[1], secret, 2, .3 if stall else 2)
        return result

    async def test_real_encrypted_roundtrip(self):
        self.assertTrue((await self.run_case())['success'])

    async def test_wrong_nonce_fails(self):
        self.assertFalse((await self.run_case(wrong_nonce=True))['success'])

    async def test_timeout_fails(self):
        result = await self.run_case(stall=True)
        self.assertFalse(result['success'])
        self.assertIn('таймаут', result['detail'])


if __name__ == '__main__':
    unittest.main()
