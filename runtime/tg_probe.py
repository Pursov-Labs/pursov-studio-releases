"""Account-free MTProto round trip through a local MTProxy; never reads chats."""
import asyncio
import hashlib
import json
import os
import struct
import sys
import time

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


def make_transport(secret, dc):
    while True:
        header = bytearray(os.urandom(64))
        if header[0] != 0xef and bytes(header[:4]) not in (b'HEAD', b'POST', b'GET ', b'OPTI', b'\xee' * 4, b'\xdd' * 4) and header[4:8] != b'\0' * 4:
            break
    header[56:60] = b'\xee' * 4
    header[60:62] = struct.pack('<h', dc)
    key_iv = bytes(header[8:56])
    enc = Cipher(algorithms.AES(hashlib.sha256(key_iv[:32] + secret).digest()), modes.CTR(key_iv[32:])).encryptor()
    reverse = key_iv[::-1]
    dec = Cipher(algorithms.AES(hashlib.sha256(reverse[:32] + secret).digest()), modes.CTR(reverse[32:])).encryptor()
    encoded = enc.update(bytes(header))
    return bytes(header[:56]) + encoded[56:], enc, dec


def request_packet(nonce):
    body = struct.pack('<I', 0xbe7e8ef1) + nonce
    message_id = int(time.time() * (1 << 32)) & ~3
    return b'\0' * 8 + struct.pack('<QI', message_id, len(body)) + body


def validate_response(data, nonce):
    if len(data) < 76 or data[:8] != b'\0' * 8:
        raise ValueError('Некорректный MTProto envelope')
    size = struct.unpack_from('<I', data, 16)[0]
    if size < 56 or size > len(data) - 20:
        raise ValueError('Некорректная длина MTProto')
    body = data[20:20 + size]
    if struct.unpack_from('<I', body)[0] != 0x05162463:
        raise ValueError('Ожидался ответ resPQ')
    if body[4:20] != nonce:
        raise ValueError('Nonce MTProto не совпал')
    # resPQ must also contain a TL pq string and RSA fingerprint vector.
    pq_size = body[36]
    if pq_size == 254 or pq_size == 0 or pq_size > 16:
        raise ValueError('Некорректное pq')
    vector_at = 36 + ((1 + pq_size + 3) // 4) * 4
    if len(body) < vector_at + 8 or struct.unpack_from('<I', body, vector_at)[0] != 0x1cb5c415:
        raise ValueError('Некорректный resPQ vector')
    count = struct.unpack_from('<I', body, vector_at + 4)[0]
    if not 1 <= count <= 64 or len(body) != vector_at + 8 + count * 8:
        raise ValueError('Некорректные RSA fingerprints')


async def probe_one(host, port, secret, dc, timeout=8):
    start = time.monotonic()
    writer = None
    try:
        async with asyncio.timeout(timeout):
            reader, writer = await asyncio.open_connection(host, port)
            header, enc, dec = make_transport(secret, dc)
            nonce = os.urandom(16)
            packet = request_packet(nonce)
            writer.write(header + enc.update(struct.pack('<I', len(packet)) + packet))
            await writer.drain()
            length = struct.unpack('<I', dec.update(await reader.readexactly(4)))[0]
            if not 40 <= length <= 65536:
                raise ValueError('Некорректный MTProxy frame')
            response = dec.update(await reader.readexactly(length))
            validate_response(response, nonce)
            return {'success': True, 'dc': dc, 'milliseconds': round((time.monotonic() - start) * 1000), 'detail': f'DC{dc}: MTProto req_pq/resPQ; без входа в аккаунт'}
    except (TimeoutError, OSError, ValueError, asyncio.IncompleteReadError) as exc:
        # Keep raw server data and credentials out of stdout/logs.
        reason = 'таймаут' if isinstance(exc, TimeoutError) else 'неверный ответ' if isinstance(exc, ValueError) else 'соединение прервано или недоступно'
        return {'success': False, 'dc': dc, 'milliseconds': round((time.monotonic() - start) * 1000), 'detail': f'DC{dc}: {reason}'}
    finally:
        if writer:
            writer.close()
            try:
                await writer.wait_closed()
            except OSError:
                pass


async def probe(config):
    if config.get('fake_tls_domain'):
        return {'success': None, 'milliseconds': 0, 'detail': 'Fake TLS: автоматический MTProto тест этого режима пока не поддерживается'}
    secret = bytes.fromhex(config['secret'])
    if len(secret) != 16:
        raise ValueError('Некорректная длина ключа')
    dc_ids = []
    for entry in config.get('dc_ip', ['2:149.154.167.220', '4:149.154.167.220']):
        dc = int(entry.split(':', 1)[0])
        if dc not in dc_ids and 1 <= dc <= 5:
            dc_ids.append(dc)
    if not dc_ids:
        dc_ids = [2, 4]
    results = await asyncio.gather(*(probe_one(config['host'], config['port'], secret, dc) for dc in dc_ids))
    return {'success': any(r['success'] for r in results), 'milliseconds': max(r['milliseconds'] for r in results), 'detail': '; '.join(r['detail'] for r in results), 'results': results}


if __name__ == '__main__':
    try:
        result = asyncio.run(probe(json.loads(sys.stdin.readline())))
    except Exception:
        result = {'success': False, 'milliseconds': 0, 'detail': 'Некорректная конфигурация MTProto теста'}
    print(json.dumps(result, ensure_ascii=True), flush=True)
