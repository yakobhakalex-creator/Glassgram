# Сторонние компоненты и лицензии

Проверено 26 ноября 2026 года по `Glassgram.csproj`, восстановленному графу NuGet и лицензионным файлам установленных пакетов. GPL-3.0 применяется к исходному коду Glassgram. Сторонние компоненты продолжают распространяться на условиях своих правообладателей.

| Компонент | Версия | Лицензия | Совместимость с GPL-3.0 и обязательства |
| --- | --- | --- | --- |
| Concentus | 2.2.2 | BSD-3-Clause | Совместима. Сохранять copyright и условия BSD-3-Clause. |
| Concentus.Oggfile | 1.0.7 | MIT | Совместима. Сохранять copyright и текст MIT. |
| NAudio | 2.2.1 | MIT | Совместима. Сохранять copyright и текст MIT. |
| TDLib и `tdlib.native.win-x64` | 1.8.67 | BSL-1.0, Apache-2.0, Zlib | Совместимы. Сохранять включённые copyright, license и notice-файлы. |
| QRCoder | 1.6.0 | MIT | Совместима. Сохранять copyright и текст MIT. |
| CommunityToolkit.WinUI.Lottie | 8.2.250604 | MIT | Совместима. Сохранять copyright и текст MIT. |
| Lottie Web Light | 5.13.0 | MIT | [Upstream](https://github.com/airbnb/lottie-web/tree/v5.13.0). Неизменённый файл `build/player/lottie_light.min.js` и лицензия в `src/Glassgram/Assets/Lottie`. |
| System.Security.Cryptography.ProtectedData | 11.0.0 | MIT | Совместима. Сохранять MIT и поставляемые third-party notices. |
| Microsoft.Win32.SystemEvents, System.Drawing.Common | 6.0.0 | MIT | Совместима. Сохранять MIT и поставляемые third-party notices. |
| Microsoft Windows App SDK, WinUI и SDK BuildTools | 1.6.241114003, 2.7.0, 10.0.26100.4654 | Microsoft Software License Terms | Использование и распространение разрешаются соответствующими условиями Microsoft. Компоненты не перелицензируются под GPL. При выпуске сохранять поставляемые `license.txt` и `NOTICE.txt`. |
| Microsoft.Web.WebView2 | 1.0.3719.77 | Microsoft Software License Terms и сторонние notices | Использование runtime и redistributable регулируется условиями Microsoft. При выпуске сохранять `LICENSE.txt` и `NOTICE.txt`. |
| Microsoft.Graphics.Win2D | 1.0.5.1 | Microsoft license terms пакета | Использование регулируется включёнными условиями пакета. Сохранять лицензионные уведомления при распространении. |

В проверенном графе зависимостей не обнаружено лицензий, запрещающих выпуск исходного кода Glassgram под GPL-3.0. Итоговая бинарная поставка должна включать лицензионные и notice-файлы всех реально упакованных компонентов. Это особенно важно для self-contained publish, поскольку он копирует runtime-компоненты в каталог выпуска.

## Адаптация кода Unigram

[Unigram](https://github.com/UnigramDev/Unigram) распространяется под GNU GPL v3. Код Unigram разрешено адаптировать для Glassgram в рамках GPL-3.0 при соблюдении следующих правил.

1. Сохранить исходный copyright и лицензионный заголовок в адаптированном файле.
2. Добавить рядом с заголовком пометку об изменении Glassgram, дату, URL исходного файла и точный commit upstream.
3. Внести в таблицу ниже исходные и конечные файлы, назначение компонента и характер изменений.
4. Не выдавать изменения Glassgram за код авторов Unigram.
5. При распространении предоставить соответствующий исходный код и этот файл уведомлений.

Адаптация выполнена из commit `9354bcb753be3b16086bd3886b8adec062ccdd91` ветки `develop`; интерфейс Unigram целиком не переносился.

| Компонент Unigram | Upstream commit и файл | Файл Glassgram | Изменения |
| --- | --- | --- | --- |
| Каталог эмодзи | `Telegram/Common/Emoticon.cs` | `src/Glassgram/Telegram/UnigramEmojiCatalog.cs` | Изолированы данные категорий, добавлена русская локализация. |
| Панель стикеров | `Telegram/ViewModels/Drawers/StickerDrawerViewModel.cs` | `src/Glassgram/MainWindow.SidePanel.cs` | Адаптированы источники TDLib: недавние, избранные, установленные наборы и поиск. |
| Общие материалы | `Telegram/Collections/MediaCollection.cs` | `src/Glassgram/MainWindow.SidePanel.cs` | Адаптирована постраничная загрузка `searchChatMessages`. |
| Профили | `Telegram/ViewModels/ProfileViewModel.cs` | `src/Glassgram/MainWindow.SidePanel.cs` | Адаптированы запросы пользователей, групп и каналов. |
| Жизненный цикл стикеров | `Telegram/Controls/Messages/Content/StickerContent.xaml.cs`, `Telegram/Controls/AnimatedImage.cs` | `src/Glassgram/MainWindow.Media.cs` | Воспроизведение WEBM/TGS связано с видимостью во viewport. |

## Noto Color Emoji

Glassgram поставляет Windows-совместимую сборку [Noto Color Emoji v2.051](https://github.com/googlefonts/noto-emoji/blob/v2.051/fonts/NotoColorEmoji_WindowsCompatible.ttf) без изменений содержимого. Copyright Google LLC, лицензия SIL Open Font License 1.1, текст в `src/Glassgram/Assets/Emoji/OFL.txt`. SHA-256 файла `19473341d23f8fdf90e91ffca381d727c43f7bc05b2758dec9687a58fbb81150`.

`apple.ttf` из Unigram в Glassgram не включён. Этот файл содержит отдельное уведомление JoyPixels / EmojiOne Free License.

## Источники лицензий

- Unigram — `https://github.com/UnigramDev/Unigram/blob/develop/LICENSE`
- TDLib — `https://github.com/tdlib/td/blob/master/LICENSE_1_0.txt`
- QRCoder — `https://github.com/codebude/QRCoder/blob/master/LICENSE.txt`
- Windows Community Toolkit — `https://github.com/CommunityToolkit/WindowsCommunityToolkit/blob/main/License.md`
- Остальные условия взяты из `.nuspec`, `LICENSE`, `NOTICE` и `THIRD-PARTY-NOTICES` файлов восстановленных NuGet-пакетов.
