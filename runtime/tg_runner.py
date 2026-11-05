"""Small process adapter for the unmodified, pinned Flowseal proxy core."""
import asyncio
import ipaddress
import json
import logging
import os
import re
import sys
import threading


def emit(event, **values):
    print(json.dumps({'event': event, **values}, ensure_ascii=True), flush=True)


def apply_config(config, core):
    from proxy.config import coerce_domain_list, parse_dc_ip_list
    host = str(config.get('host', '127.0.0.1'))
    if not ipaddress.ip_address(host).is_loopback:
        raise ValueError('Разрешён только локальный адрес MTProxy')
    port = config.get('port', 1443)
    if type(port) is not int or not 1024 <= port <= 65535:
        raise ValueError('Некорректный порт MTProxy')
    secret = config.get('secret', '')
    if not isinstance(secret, str) or not re.fullmatch('[0-9a-fA-F]{32}', secret):
        raise ValueError('Некорректный ключ MTProxy')
    pc = core.proxy_config
    pc.host, pc.port, pc.secret = host, port, secret
    pc.dc_redirects = parse_dc_ip_list(config.get('dc_ip', ['2:149.154.167.220', '4:149.154.167.220']))
    pc.buffer_size = max(4, min(4096, int(config.get('buf_kb', 256)))) * 1024
    pc.pool_size = max(0, min(32, int(config.get('pool_size', 4))))
    pc.fallback_cfproxy = bool(config.get('cfproxy', True))
    pc.cfproxy_h2_media = bool(config.get('h2', True))
    pc.cfproxy_user_domains = coerce_domain_list(config.get('cfproxy_user_domain', [])) if config.get('cfproxy_user_domain_enabled', True) else []
    pc.cfproxy_worker_domains = coerce_domain_list(config.get('cfproxy_worker_domain', [])) if config.get('cfproxy_worker_enabled', True) else []
    pc.disable_secure = bool(config.get('no_secure', False))
    pc.force_test_dc = bool(config.get('force_test_dc', False))
    pc.fake_tls_domain = str(config.get('fake_tls_domain', ''))
    pc.proxy_protocol = False


async def run(source, config):
    sys.path.insert(0, source)
    import proxy.tg_ws_proxy as core
    apply_config(config, core)
    logging.getLogger().handlers = [logging.NullHandler()]
    logging.getLogger().setLevel(logging.CRITICAL)
    stop = asyncio.Event()
    task = asyncio.create_task(core._run(stop))
    for _ in range(400):
        if task.done():
            await task
            raise RuntimeError('MTProxy завершился до запуска')
        if getattr(core, '_server_instance', None):
            emit('ready', port=core.proxy_config.port)
            break
        await asyncio.sleep(.05)
    else:
        stop.set()
        raise TimeoutError('MTProxy не открыл порт')

    queue = asyncio.Queue()
    loop = asyncio.get_running_loop()

    def read_commands():
        pending = b''
        try:
            while True:
                chunk = os.read(sys.stdin.fileno(), 4096)
                if not chunk:
                    loop.call_soon_threadsafe(queue.put_nowait, None)
                    return
                pending += chunk
                while b'\n' in pending:
                    line, pending = pending.split(b'\n', 1)
                    loop.call_soon_threadsafe(queue.put_nowait, line)
        except (OSError, RuntimeError):
            return

    threading.Thread(target=read_commands, daemon=True, name='control-input').start()

    async def commands():
        while True:
            line = await queue.get()
            if line is None:
                stop.set()
                return
            try:
                if json.loads(line).get('command') == 'stop':
                    stop.set()
                    return
            except (ValueError, AttributeError):
                continue

    control = asyncio.create_task(commands())
    done, _ = await asyncio.wait([task, control], return_when=asyncio.FIRST_COMPLETED)
    stop.set()
    try:
        await asyncio.wait_for(task, 5)
    finally:
        control.cancel()
    emit('stopped')


if __name__ == '__main__':
    try:
        command = json.loads(sys.stdin.readline())
        asyncio.run(run(command['source'], command['config']))
    except Exception as exc:
        emit('error', detail='TG worker: ' + type(exc).__name__)
        sys.exit(1)
