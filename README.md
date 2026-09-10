# Offline Log Reader

Desktopová aplikace pro Windows, napsaná ve WPF a .NET 9. Pracuje výhradně s místními soubory, nepoužívá síť ani telemetrii a zdrojové logy nemění.

## Spuštění

Spusťte `artifacts/OfflineLogReader/OfflineLogReader.exe`. Složku lze zkopírovat na jiný počítač jako celek; balíček vyžaduje nainstalovaný **.NET 9 Desktop Runtime pro Windows x64**. Instalátor ani přibalený runtime nejsou součástí této verze.

Pro vývoj je potřeba .NET 9 SDK a Windows:

```powershell
dotnet build OfflineLogReader.sln -c Release
dotnet run --project OfflineLogReader -c Release
```

## Ovládání

1. Zvolte kódování **UTF-8** nebo **Windows-1250** a použijte **Přidat soubory**, Ctrl+O nebo přetažení souborů na okno. V dialogu můžete vybrat více souborů. Přidání dalších souborů sloučí všechny záznamy a vymaže hlavní filtry; již otevřená výsledková okna si zachovají původní data.
2. Nastavte období, severity a source a stiskněte **Použít filtry**. Datum má formát `dd.MM.yyyy`, volitelně s časem `HH:mm:ss`. Datum bez času v poli Do zahrnuje celý den. Hranice jsou včetně.
3. Severity vybírejte jednotlivými kliknutími, source zaškrtávacími políčky. Prázdný výběr znamená všechny hodnoty. Vyhledávání v seznamu source nemění dříve zaškrtnuté zdroje.
4. Tlačítkem **Vylučovací filtr** přidejte libovolný počet podmínek „záznam obsahuje text“. `Aa` znamená rozlišování velikosti písmen. Podmínku lze vypnout nebo odstranit. Změny potvrďte tlačítkem **Použít filtry**.
5. **Ctrl+F** zaměří hledání. Všechny viditelné shody se zvýrazní. **Enter** přejde na další odpovídající záznam, **Shift+Enter** na předchozí; procházení se cyklicky opakuje. Escape hledání vymaže. Hledá se v celé hlavičce i zprávě včetně stack trace.
6. **Find All** otevře samostatné okno pouze s odpovídajícími záznamy. Hledání standardně používá aktuálně zobrazenou sadu; přepínač **Všechny záznamy** prohledá i skryté záznamy. Každé výsledkové okno zachovává vlastní snímek výsledků. Jeho textové pole mění pouze zvýraznění, nikoliv obsah sady.
7. Vyberte řádek pro úplný detail a původní soubor s číslem fyzického řádku. **Zobrazit v kontextu** otevře chronologickou sadu bez filtrů s vybraným záznamem; okolí lze procházet oběma směry. **Kopírovat záznam** zkopíruje celý původní záznam.
8. Dlouhou operaci lze zrušit tlačítkem **Zrušit operaci**. Při zrušení nebo chybě načítání zůstane původní sada zachována. **Zavřít soubory** zavře také výsledková okna a uvolní soubory.

## Pravidla parsování

```text
03.09.2026 21:49:37 - [ERROR] - [R-D-5] - Download failed
System.IO.IOException: Connection closed
   at Downloader.Worker.Run()
```

- Hlavička určuje datum, čas, severity a source. Název souboru datum neurčuje.
- Hodiny mohou být jednociferné i dvouciferné (`0:00:00`, `9:59:59`, `09:59:59`).
- Řádky bez platné hlavičky pokračují v předchozím záznamu. Hlavičky se očekávají na začátku fyzického řádku. Preambule před první hlavičkou zůstává jako záznam `UNKNOWN` bez času.
- Nerozpoznané záznamy se řadí před datované záznamy; časové filtry je vyřadí.
- Čas se interpretuje přesně tak, jak je uložený, bez převodu časových zón.
- Shody času rozhoduje pořadí přidání souborů a pozice uvnitř souboru. Duplicitní záznamy z různých souborů se zachovávají. Opětovné přidání stejné cesty se ignoruje.
- Podporovány jsou UTF-8 s BOM i bez BOM, Windows-1250 a konce řádků LF/CRLF. UTF-16/UTF-32 nejsou podporovány.
- Různé skupiny filtrů se spojují pomocí AND, více severity nebo source pomocí OR. Shoda s kterýmkoliv aktivním vylučovacím filtrem skryje celý víceřádkový záznam.

## Výkon a omezení

Index obsahuje metadata a bajtové pozice záznamů. Text se načítá podle potřeby ze souborů otevřených pouze ke čtení. Tabulka virtualizuje řádky i sloupce a náhledy mají omezenou mezipaměť. Načítání, řazení, dotazy i načtení detailu probíhají na pozadí. Každé výsledkové okno drží pouze odkazy na záznamy, nekopíruje celý text.

Spotřeba paměti závisí hlavně na počtu záznamů, nikoliv pouze na velikosti souborů. Index není uložen na disku a při dalším spuštění se vytváří znovu. Změny logů během prohlížení nejsou podporovány.

Detail mimořádně dlouhého záznamu je omezený na 256 KiB a tuto skutečnost viditelně hlásí. Kopírování čte celý záznam. V detailu se zvýrazní nejvýše 10 000 výskytů, další text zůstává viditelný. Vyhledávání vždy prochází celý záznam; u záznamů nad 1 MiB čte po blocích a umožňuje průběžné zrušení. Jednorázové kopírování záznamu nad 2 GiB není podporováno.

První verze neobsahuje export, ukládání filtrů, regulární výrazy, automatické sledování změn ani instalátor.

## Ověření

Testovací projekt je samostatný konzolový runner bez balíčků třetích stran. Při selhání skončí nenulovým kódem.

```powershell
dotnet run --project OfflineLogReader.Tests -c Release
dotnet run --project OfflineLogReader.Tests -c Release -- --benchmark-mb 600
dotnet run --project OfflineLogReader.Tests -c Release -- --benchmark-mb 2048
```

Benchmark vytvoří syntetický soubor v dočasné složce a po skončení jej odstraní. Požaduje odpovídající volné místo na disku. Časy ovlivňuje disková cache a konkrétní hardware; nejde o zaručené limity.

Naměřeno během implementace na tomto počítači, Release sestavení:

| Data | Záznamy | Indexování | Řazení | Filtr ERROR | Celotextové hledání | Špička paměti procesu |
|---|---:|---:|---:|---:|---:|---:|
| 600 MiB | 1 427 812 | 1,37 s | 0,17 s | 0,02 s | 2,18 s | 243 MiB |
| 2 GiB | 4 873 600 | 3,73 s | 0,60 s | 0,05 s | 7,61 s | 623 MiB |

Tyto hodnoty měří jádro v konzolovém procesu, bez nákladů WPF rozhraní. Automatické testy zahrnují víceřádkové záznamy, UTF-8/BOM, Windows-1250, řazení a shody času, kombinace filtrů, hledání ve stack trace, hranice data, nerozpoznané záznamy, přechod přes hranice bufferu, dlouhé záznamy a rušení operací.

V běžící aplikaci bylo ověřeno načtení dvou souborů, sloučení 12 záznamů, Ctrl+F, navigace, zvýraznění v tabulce i detailu, Find All a přechod do kontextu. Ukázkové soubory jsou ve složce `samples`. Drag & drop a všechny kombinace ovládacích prvků nebyly samostatně ručně otestovány.

## Struktura

- `OfflineLogReader.Core` – index, parser, stabilní řazení, dotazy a čtení textu.
- `OfflineLogReader` – WPF rozhraní, řízení operací a výsledková okna.
- `OfflineLogReader.Tests` – regresní testy a zátěžový benchmark.
- `samples` – dva malé denní logy pro vyzkoušení.

Lokální distribuční balíček vytvoříte příkazem:

```powershell
dotnet publish OfflineLogReader/OfflineLogReader.csproj -c Release --no-self-contained -o artifacts/OfflineLogReader
```

