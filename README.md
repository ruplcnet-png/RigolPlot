# RigolPlot

Даталоггер для осциллографа **Rigol DHO800/900** (DHO914S/DHO924S и аналоги) через SCPI по TCP/VXI-11. Файл исходника, Windows, WinForms, .NET Framework 4.x.

![Язык](https://img.shields.io/badge/язык-C%23-blue)
![.NET](https://img.shields.io/badge/.NET-Framework%204.x-512BD4)

## Возможности

- Опрос измерений (VRMS, VAVG) по выбранным каналам CH1–CH4;
- Интервал записи от 10 мс (по умолчанию 100 мс);
- Запись в CSV (`rigol_ch1_vavg_<дата>.csv`);
- Пороги с тревогой и звуком;
- Экспорт графика в JPG;
- Тёмная/светлая темы;

## Сборка

Компилятор C# из .NET Framework SDK (csc.exe):

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /target:winexe /optimize+ `
  /out:"RigolPlot.exe" `
  /win32manifest:"RigolPlot.manifest" `
  "RigolPlot.cs"
```

Приложение рассчитано на запуск под Windows, exe без зависимостей.

## Использование

1. Укажите IP осциллографа (порт 5555), выберите интервал, каналы и величину;
2. «Пуск» — начнётся запись и отрисовка графика;
3. «Стоп» — остановка;
4. «Открыть CSV…» — просмотр записанного файла; «Экспорт JPG» — сохранение графика в картинку;
5. Изменения масштаба прибора (развёртка, единицы) подхватываются автоматически.

Формат CSV: заголовок `time_s;voltage_<param>_chN;...`, значения через `;`, время в секундах от начала записи.

## SCPI-команды

| Назначение | Команда |
|---|---|
| Измерение | `:MEAS:ITEM? VRMS,CHAN1` |
| Единица канала | `:CHAN1:UNIT?` (`VOLT`/`AMP`/`WATT`/`UNKN`) |
| Развёртка | `:TIMebase:SCAL?` (с/дел) |

## Структура

- `RigolPlot.cs` — весь исходник (WinForms, графика, SCPI);
- `RigolPlot.manifest` — манифест DPI-awareness;
- `RigolPlot.exe` — приложение;
