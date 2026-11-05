# Компоненты сторонних авторов

PURSOV STUDIO использует открытые компоненты и сохраняет их авторство. Полные тексты лицензий находятся в `payload/licenses`, рядом с Telegram-прокси и в метаданных Python-пакетов. Зафиксированные версии, ссылки на исходники и SHA-256 архивов перечислены в `payload/sources.lock.json`, `payload/zapret.lock.json` и `runtime/tg-dependencies.lock.json`.

| Компонент | Источник | Лицензия / уведомления |
|---|---|---|
| Zapret Windows scripts | https://github.com/Flowseal/zapret-discord-youtube | MIT; оригинальный LICENSE включается в payload |
| zapret / winws v72.9 | https://github.com/bol-van/zapret/tree/c849e55ef0f1c244206f5a05ff7b1ab41a3824ee | MIT; исходники и оригинальный текст лицензии включены в выпуск |
| WinDivert | https://github.com/basil00/WinDivert/tree/v2.2.2 | Выбраны условия LGPLv3; полный исходный текст двойной лицензии upstream сохранён |
| Cygwin DLL 3.4.10 | https://github.com/cygwin/cygwin/tree/cygwin-3.4.10 | LGPLv3 или более поздняя версия с исключением для связывания; исходники, COPYING, COPYING.LIB и CYGWIN_LICENSE включены в выпуск |
| TG WS Proxy core 1.11.4 | https://github.com/Flowseal/tg-ws-proxy | MIT; LICENSE включён в payload/tg |
| CPython 3.12.10 | https://www.python.org/downloads/release/python-31210/ | PSF License; LICENSE.txt включён в runtime/python |
| cryptography | https://github.com/pyca/cryptography | Apache-2.0 OR BSD-3-Clause; лицензии wheel в dist-info |
| httpx, httpcore, h11, h2, hpack, hyperframe, anyio, idna, sniffio, typing_extensions, cffi, pycparser, certifi | Пакеты из официального PyPI; точные версии/хеши в runtime/tg-dependencies.lock.json | Оригинальные LICENSE/metadata в dist-info runtime/python/Lib/site-packages |
| .NET и WPF | https://github.com/dotnet/runtime и https://github.com/dotnet/wpf | MIT и ThirdPartyNotices; входят в self-contained publish |

Архив `PURSOV_STUDIO_<версия>_third_party_sources.zip` в том же GitHub Release содержит полные исходные архивы Flowseal Zapret, TG WS Proxy, winws, WinDivert и Cygwin по указанным ревизиям. Архив исходников оболочки содержит скрипты получения этих компонентов и сборки установщика.

WinDivert и Cygwin загружаются из отдельных файлов. Изменение их исходников, сборка совместимых замен и отладка таких изменений допускаются согласно их лицензиям. Для установки изменённых системных компонентов нужны права администратора и соблюдение требований Windows к подписи драйверов. Собственную сборку оболочки и канал обновлений можно собрать из открытых исходников со своим ключом.

PURSOV STUDIO запускает ядро Telegram через небольшой управляющий runner и выполняет отдельную транспортную проверку. Telegram-аккаунты, Discord-токены и пользовательские сообщения для этой проверки не используются.
