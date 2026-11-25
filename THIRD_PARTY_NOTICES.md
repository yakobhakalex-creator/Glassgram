# Сторонние компоненты и лицензии

Проверено 25 ноября 2026 года по `Glassgram.csproj`, восстановленному графу NuGet и лицензионным файлам установленных пакетов. GPL-3.0 применяется к исходному коду Glassgram. Сторонние компоненты продолжают распространяться на условиях своих правообладателей.

| Компонент | Версия | Лицензия | Совместимость с GPL-3.0 и обязательства |
| --- | --- | --- | --- |
| TDLib и `tdlib.native.win-x64` | 1.8.67 | BSL-1.0, Apache-2.0, Zlib | Совместимы. Сохранять включённые copyright, license и notice-файлы. |
| QRCoder | 1.6.0 | MIT | Совместима. Сохранять copyright и текст MIT. |
| CommunityToolkit.WinUI.Lottie | 8.2.250604 | MIT | Совместима. Сохранять copyright и текст MIT. |
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

## Источники лицензий

- Unigram — `https://github.com/UnigramDev/Unigram/blob/develop/LICENSE`
- TDLib — `https://github.com/tdlib/td/blob/master/LICENSE_1_0.txt`
- QRCoder — `https://github.com/codebude/QRCoder/blob/master/LICENSE.txt`
- Windows Community Toolkit — `https://github.com/CommunityToolkit/WindowsCommunityToolkit/blob/main/License.md`
- Остальные условия взяты из `.nuspec`, `LICENSE`, `NOTICE` и `THIRD-PARTY-NOTICES` файлов восстановленных NuGet-пакетов.
