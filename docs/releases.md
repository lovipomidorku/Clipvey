# Релизы и обновления

Этот файл — договорённость между сборкой релизов (GitHub Actions) и проверкой обновлений в обоих приложениях.

## Версия

- Единственный источник — файл `VERSION` в корне: одна строка вида `0.2.0` (семантическое версионирование, без `v`).
- `mac/scripts/build.sh` и `windows/Directory.Build.props` (для всех проектов .NET) читают версию из него.
- Тег релиза — `v` + версия, например `v0.2.0`.

## Файлы релиза

| Файл | Что это |
|---|---|
| `Clipvey-mac.zip` | `Clipvey.app`, упакованный `ditto -c -k --keepParent` |
| `Clipvey.exe` | Windows x64, один файл |
| `SHA256SUMS` | Строки `<sha256 hex строчными>  <имя файла>` (два пробела), по одной на каждый файл выше, `\n` в конце каждой строки |
| `SHA256SUMS.sig` | Подпись `SHA256SUMS`: base64 от 64 байт `r ‖ s`, ECDSA P-256 с SHA-256 |

Подпись проверяется открытым ключом, зашитым в оба приложения (X9.63, 65 байт, base64):

```
BH2yxPlYNbki9eTLDttU3YTv5qjUJ8Biz4ChVq7y7OdeOqkmmtYi5Zch3XAfrF0x4qSrNvvNOaFMqMONHERTJJs=
```

- Mac: `P256.Signing.PublicKey(x963Representation:)` и `isValidSignature(P256.Signing.ECDSASignature(rawRepresentation:), for:)`.
- Windows: `ECDsa.ImportSubjectPublicKeyInfo` не нужен — `ECParameters` с `Q.X`/`Q.Y` из байтов 1…32 и 33…64, затем `VerifyData(data, sig, HashAlgorithmName.SHA256)` (формат подписи по умолчанию — IEEE P1363, то есть `r ‖ s`).
- Закрытый ключ хранится вне репозитория (base64 от 32 байт, `rawRepresentation`). В GitHub Actions — секрет `CLIPVEY_SIGNING_KEY`. Подписывает `scripts/sign-release.swift`.

## Проверка обновлений

- Адрес: `https://api.github.com/repos/lovipomidorku/Clipvey/releases/latest`, заголовки `Accept: application/vnd.github+json` и `User-Agent: Clipvey/<версия>`.
- В режиме проверки адрес переопределяется флагом `--update-url URL` (Mac: вместе с `--test`; Windows: тем же флагом), чтобы гонять обновление против локального HTTP-сервера. Сервер отдаёт такой же JSON и файлы.
- Из ответа берутся `tag_name` и `assets[].name` / `assets[].browser_download_url`. Черновики и пре-релизы в `releases/latest` не попадают.
- Когда: через минуту после запуска и затем раз в сутки. Настройка «Проверять обновления автоматически» (по умолчанию включена) и кнопка «Проверить сейчас».
- 404, отсутствие релизов, нет сети, лимит запросов — не ошибка для пользователя: тихо в журнал, следующая попытка по расписанию. При ручной проверке показать короткое сообщение.
- Новее, если версия из `tag_name` (без `v`) больше текущей при сравнении по числам `major.minor.patch`.

## Установка

Скачивание только после согласия пользователя («Доступна версия X — обновить?»).

1. Скачать `SHA256SUMS`, `SHA256SUMS.sig` и нужный файл по HTTPS во временную папку.
2. Проверить подпись `SHA256SUMS`, затем SHA-256 файла по строке из `SHA256SUMS`. Любое несовпадение — отказ, файл удаляется, сообщение пользователю.
3. **Mac.** Распаковать `Clipvey-mac.zip` (`ditto -x -k`) во временную папку, проверить, что внутри `Clipvey.app` с тем же `CFBundleIdentifier` и версией из тега. Запустить отсоединённый скрипт `/bin/sh`, который ждёт завершения процесса, заменяет текущий `.app` (путь `Bundle.main.bundleURL`) новым и открывает его. Затем приложение завершается.
4. **Windows.** Переименовать работающий `Clipvey.exe` в `Clipvey.exe.old`, положить новый на его место, запустить его и завершиться. При запуске удалить `Clipvey.exe.old`, если он есть.
