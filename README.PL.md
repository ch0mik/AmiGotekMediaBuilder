# AmiGotekMediaBuilder — C#/.NET

**Język:** polski | [English](README.md)

Migracja narzędzia ami-gotek-media-builder do C# i .NET 10. Obecna wersja
obejmuje skanowanie plików ADF/DSK/ZIP, parsowanie nazw, grupowanie wydań,
katalog JSONL, wielosystemowy cache SQLite metadanych i artworku, pobieranie
online, NFO oraz bezpieczny eksport do stagingu Gotek.

## Wymagania

- Windows 10/11, Linux (desktop) lub macOS dla GUI (Avalonia);
- .NET SDK 10.0 lub nowszy;
- katalog źródłowy z plikami `.adf`, `.dsk` lub `.zip` (nie musi mieć nazwy
  `original/`);
- źródłowe pliki `.adf`, `.dsk` lub `.zip` muszą być dostarczone przez operatora.

Wskazany `library_root` jest skanowany bezpośrednio, jeśli nie podasz
`original_dir`. Parametr `original_dir` pozostaje opcjonalnym sposobem wskazania
oddzielnego katalogu źródłowego dla istniejących konfiguracji. Pliki wejściowe
są tylko odczytywane; gdy źródłem jest cały root, katalogi zarządzane przez
program (staging, assets, catalog itd.) są automatycznie pomijane.

W głównym GUI katalog roboczy oznacza bazę zawierającą `assets`, `catalog` i
`work`. Jeżeli wskażesz istniejący katalog `assets` albo `catalog`, aplikacja
automatycznie użyje jego rodzica jako bazy, aby nie utworzyć drzewa
`assets\assets`.

### Motyw i font GUI

Główne GUI działa w trybie dark z granatowo-niebieskimi akcentami. Używa
lokalnie dołączonego fontu [Zerove](https://ggbot.itch.io/zerove-font) —
czytelnego, nowoczesnego kroju z lekkim retro-technologicznym charakterem,
CC0 1.0 i obsługą polskich znaków. Dzięki osadzeniu pliku TTF wygląd nie zależy
od fontów zainstalowanych w Windows, Linux ani macOS. Informacje o źródle,
licencji i sumie pliku znajdują się w [THIRD_PARTY.md](THIRD_PARTY.md).

## Budowanie

W katalogu repozytorium:

~~~powershell
dotnet build AmiGotekMediaBuilder.slnx
dotnet test AmiGotekMediaBuilder.Core.Tests\AmiGotekMediaBuilder.Core.Tests.csproj --no-build --no-restore
~~~

Kod obsługi produkcji demoscenowych znajduje się w osobnym projekcie
`AmiGotekMediaBuilder.Demoscene`. Programy `AmiGotekMediaBuilder.Demoscene.Cli`
i `AmiGotekMediaBuilder.Demoscene.Gui` są niezależne od głównego CLI/GUI.
Budowanie solution kompiluje wszystkie warianty.

## Inicjalizacja biblioteki

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- init --library-root C:\AmigaLibrary --config C:\AmigaLibrary\config\config.toml
~~~

Polecenie zapisuje konfigurację i tworzy zarządzane katalogi. Pliki obrazów
możesz umieścić bezpośrednio w `C:\AmigaLibrary` albo wskazać oddzielny katalog:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- init --library-root C:\AmigaLibrary --original-dir C:\AmigaImages
~~~

Domyślny układ:

~~~text
C:\AmigaLibrary\
├── *.adf, *.dsk, *.zip        # wejście może leżeć bezpośrednio w rootu
├── original\                 # opcjonalny osobny katalog wejściowy
├── catalog\
│   ├── catalog.db             # wewnętrzny cache SQLite (hash/provider/artwork)
│   ├── scan.jsonl
│   ├── parse.jsonl
│   ├── groups.jsonl
│   └── metadata-cache\
├── assets\
│   ├── nfo\
│   ├── artwork-original\    # oryginalne bajty pobrane od providera
│   └── artwork-processed\   # kopia używana w eksporcie Gotek (lub fallback)
├── work\staging\
├── output\
├── unknown\
├── review\
├── config\manual-approvals\
├── demoscene\
│   ├── downloads\           # pobrane ADF/DSK z Pouët, Demozoo i mirrorów
│   ├── metadata\            # pełne rekordy produkcji JSON
│   └── cache\
├── reports\
└── logs\
~~~

## Sprawdzanie konfiguracji

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- config show --config C:\AmigaLibrary\config\config.toml
dotnet run --project AmiGotekMediaBuilder.Cli -- config validate --config C:\AmigaLibrary\config\config.toml
~~~

Konfiguracja może również wskazywać role bezpośrednio:

~~~toml
library_root = "C:\\AmigaLibrary"
# optional; when omitted, library_root itself is scanned
original_dir = "C:\\AmigaLibrary\\original"
staging_dir = "C:\\AmigaLibrary\\work\\staging"
output_dir = "C:\\AmigaLibrary\\output"
quarantine_dir = "C:\\AmigaLibrary\\unknown"
cache_dir = "C:\\AmigaLibrary\\catalog\\metadata-cache"
demoscene_dir = "C:\\AmigaLibrary\\demoscene"
# optional: SQLite created from a GameBase .mdb (Skyscraper mdb2sqlite helper)
gamebase_db = "C:\\Databases\\AmigaGameBase.sqlite3"
~~~

Przy rozwiązywaniu ścieżek obowiązuje kolejność: argument CLI, zmienna
środowiskowa AMIGA_ADF_* i plik --config.

## Skanowanie

Skanowanie jest operacją tylko do odczytu:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- scan --library-root C:\AmigaLibrary
dotnet run --project AmiGotekMediaBuilder.Cli -- scan --library-root C:\AmigaLibrary --json
~~~

Program rekursywnie oblicza SHA-256 każdego pliku .adf/.dsk oraz wpisów .adf/.dsk
znalezionych w archiwach .zip, a następnie parsuje tytuł,
rok, język, chipset, wersję, grupę crack, numer dysku, edycję i role dysków
specjalnych.

Archiwa ZIP są odczytywane bez rozpakowywania i bez modyfikacji. W katalogu
wirtualna ścieżka wpisu ma postać collection.zip::Game.adf.

### Zasady grupowania wydań

Program odróżnia katalog pojedynczej gry od katalogu kolekcji. Jeśli katalog
zawiera różne tytuły, każda gra jest grupowana osobno według znormalizowanego
tytułu, edycji, wersji i formatu obrazu. Katalogi indeksowe `A`–`Z` również nie
są traktowane jako nazwy gier.

Katalog zawierający jedno wydanie pozostaje jego granicą. Dzięki temu obrazy o
ogólnych nazwach, takich jak `Disk 1`, `Boot` i `Data`, mogą zostać połączone z
właściwą grą na podstawie nazwy katalogu. Alternatywne obrazy tego samego
numeru dysku są rozdzielane na osobne warianty; niejednoznaczne lub niepełne
zestawy trafiają do ręcznej weryfikacji zamiast być łączone losowo.

## Build offline

build wykonuje scan → parse → group, zapisuje katalog JSONL oraz generuje
offline cache metadanych (JSON + SQLite), pliki NFO i domyślne miniaturki gier:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- build --library-root C:\AmigaLibrary --json
~~~

Nie są wykonywane żadne żądania sieciowe. Jeżeli gra nie ma lokalnego artworku,
pipeline kopiuje wbudowaną grafikę `default-game-artwork.jpg` do
`assets\artwork-original` i `assets\artwork-processed`; nie dotyczy to demosceny.

## Wewnętrzny cache SQLite

`init` tworzy bazę `catalog/catalog.db`. Jest to jedna baza dla wielu systemów
i typów zawartości; rekordy są rozdzielone przez `system_id`, `platform` oraz
`content_type` (`game`, `demo`, `tool`). Głównym kluczem wyszukiwania jest
SHA-256 każdego obrazu dysku (a dla zestawu — hashe wszystkich jego dysków),
z fallbackiem do `release_key` dla starych lub niepełnych rekordów.

Podczas pierwszego `build --online` brakujące hashe są wysyłane do providerów,
a opisy, wyniki providerów i lokalne ścieżki artworków są zapisywane w
SQLite. Przy następnym buildzie pipeline działa cache-first: każdy poprawny
wynik providera — również wynik zawierający metadata bez artworku — jest
używany lokalnie bez ponownego scrapowania. Zapisany URL może posłużyć do
odtworzenia brakującego lokalnego obrazka bez ponownego wyszukiwania gry.
Zmieniona nazwa pliku lub katalogu nie powoduje ponownego pobrania, jeśli hash
się nie zmienił.

Przykładowe podsumowanie online:

~~~text
catalog cache: 412 hit(s), 7 queried
~~~

Uruchomienie tego samego polecenia `build --online` ponownie dla niezmienionych
plików powinno zwiększyć liczbę `hit(s)` i nie wykonywać tych samych pobrań.

Dotychczasowe pliki `catalog/metadata-cache/metadata-*.json` pozostają
czytelne jako kompatybilny fallback. Podczas builda znaleziony rekord JSON jest
automatycznie zapisywany także do SQLite; pliki JSON nie są usuwane.
`GameBase` pozostaje osobnym, lokalnym źródłem danych i nie jest mieszany z
wewnętrznym cache’em wyników providerów.

## Providery online

Flaga `--online` włącza pipeline przeznaczony dla gier: lokalny GameBase,
ScreenScraper, OpenRetro, TheGamesDB oraz artwork Libretro. ScreenScraper jest
włączany przez `SCREENSCRAPER_DEV_ID` i `SCREENSCRAPER_DEV_PASSWORD`;
opcjonalne konto użytkownika konfiguruje się przez `SCREENSCRAPER_USER` i
`SCREENSCRAPER_PASSWORD`. TheGamesDB jest włączany przez
`THEGAMESDB_API_KEY`. OpenRetro i Libretro nie wymagają credentiali.

Pipeline najpierw używa identyfikacji pliku (SHA-1 i MD5 w ScreenScraper),
potem ścisłego dopasowania tytułu i platformy Amiga. Wszystkie źródła są
sprawdzane, a brakujące pola są scalane. Sam URL obrazka nie kończy
wyszukiwania: błąd HTTP, HTML zamiast obrazu albo uszkodzony plik powoduje
przejście do następnego providera.

Brak dopasowania albo brak obrazka u providera nie blokuje eksportu. Build
online nie zapisuje placeholdera w cache artworku, dzięki czemu nie udaje on
udanego pobrania i nie zasłania późniejszych wyników. Podczas eksportu
wbudowany `default-artwork` jest zapisywany bezpośrednio obok NFO, jeśli nie ma
zweryfikowanej grafiki gry. Demoscene ma osobny katalog miniaturek i pozostaje
wyłączona z tego fallbacku.

Jeżeli rekord cache zawiera wynik providera z metadata, ale bez artworku,
kolejny build nie odpytuje ponownie providerów. Gry, dla których żaden provider
nie znalazł dopasowania i zapisano jedynie `offline-filename`, mogą zostać
sprawdzone ponownie w późniejszym buildzie.

Rekordy gry zapisane przez starsze wersje z providerem `pouet` lub `demozoo`
są traktowane jako nieaktualne: pipeline ponawia wyszukiwanie w providerach
gier i nie przenosi tytułu, opisu ani miniaturki demoscenowej do katalogu gry.

Pouët jest osobnym providerem demoscenowym i nie jest włączany w zwykłym
pipeline gier. Dzięki temu miniaturka dema nie może trafić do katalogu gry o
podobnej nazwie. Katalog Pouët/Demozoo obsługuje osobne menu GUI oraz komendę
`demoscene`; miniaturki są zapisywane wyłącznie w `assets\\demoscene\\`.
Adres Pouët można zmienić przez `POUET_BASE_URL`.

OpenRetro wykonuje wyszukiwanie po tytule Amiga na `openretro.org`, pobiera
kanoniczny opis, rok, wydawcę oraz okładkę (front image; gdy jej brak — pierwszy
screen). ScreenScraper oraz TheGamesDB korzystają z oficjalnych API, a Libretro
dostarcza końcowego, bezkluczowego kandydata grafiki.
Podstawowy transport żądań metadata nie podąża za redirectami, blokuje prywatne
adresy i ogranicza rozmiar odpowiedzi. Po znalezieniu URL
obrazka zapisuje go do `assets\artwork-original\<Release>.<ext>`, tworzy kopię
`assets\artwork-processed\<Release>.<ext>` oraz plik `.source.json` z proweniencją.
W eksporcie obraz jest kopiowany obok `.nfo` do folderu release. Kopia
processed zachowuje bajty i format źródłowy (JPG/PNG/WEBP/GIF); skalowanie można
wykonać później bez utraty zachowanego mastera.

Pobieranie artworku obsługuje do trzech bezpiecznie zweryfikowanych redirectów,
korzysta z systemowego proxy i odrzuca odpowiedzi, które nie mają rozpoznawalnego
podpisu JPG/PNG/WEBP/GIF (np. stronę CAPTCHA zapisaną jako `.jpg`).

Przykład — publiczny łańcuch online spróbuje wszystkich wbudowanych providerów
i zapisze znaleziony artwork:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- build --library-root C:\AmigaLibrary --online
~~~

### GameBase DB (lokalny provider)

GameBase jest lokalnym źródłem danych — nie wykonuje żądań sieciowych i nie
wymaga żadnych credentiali. Skyscraper dostarcza skrypt `mdb2sqlite.sh`, który
konwertuje plik `.mdb` do SQLite; wskaż wynikowy plik przez `--gamebase-db`, wpis
`gamebase_db` w TOML albo zmienną `AMIGA_ADF_GAMEBASE_DB`:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- init --library-root C:\AmigaLibrary --gamebase-db C:\Databases\AmigaGameBase.sqlite3
dotnet run --project AmiGotekMediaBuilder.Cli -- build --library-root C:\AmigaLibrary --config C:\AmigaLibrary\config\config.toml --online
~~~

Provider rozpoznaje typowe schematy tabel GameBase po nazwie, pliku/ROM-ie i
tytule. Obrazy wskazane w kolumnach `Screenshot`, `Cover` lub podobnych są
szukane względem katalogu SQLite (m.in. `Screenshots`, `Cover`, `Images`), a
następnie kopiowane do `assets\\artwork-original` i
`assets\\artwork-processed`. Brak pliku DB lub brak dopasowania jest bezpiecznym
brakiem wyniku — pozostałe providery nadal działają.

Opis modułów Skyscrapera: [OpenRetro](https://gemba.github.io/skyscraper/SCRAPINGMODULES/#openretro)
i [GameBase DB](https://gemba.github.io/skyscraper/SCRAPINGMODULES/#gamebase-db).
Do testów lub lokalnego mirroru można ustawić `OPENRETRO_BASE_URL`.

Jeżeli publiczny endpoint zwróci błąd, build przechodzi do kolejnego providera
lub metadanych filename-only; nie blokuje to skanowania ani eksportu.

## Demoscene: osobny program z Pouët i Demozoo

Program `AmiGotekMediaBuilder.Demoscene` kataloguje produkcje Amiga z Pouët oraz Demozoo według platformy
`ocs-ecs`, `aga` albo `ppc-rtg`. Pobierane są metadane, opis i miniaturka oraz
bezpośrednie obrazy ADF/DSK. Obsługiwane są również ZIP-y i GZip zawierające
ADF/DSK. DMS, LHA, ROM-y, FTP oraz pliki wykonywalne są raportowane jako
pominięte — nie są uruchamiane ani konwertowane.

Demozoo jest odpytywane przez filtr production_type=1 (Demo). Identyfikator
Pouët znaleziony na stronie Demozoo jest używany do deduplikacji; gdy linku
brakuje, stosowana jest zgodność tytułu, grupy, roku i platformy. Przy
duplikacie preferowany jest rekord Pouët, a brakujące linki do pobrania są
do niego dołączane. Platformy Demozoo odpowiadają OCS/ECS (5), AGA (6) oraz
PPC/RTG (26).

Opcjonalnie można ustawić DEMOZOO_BASE_URL, np. dla lokalnego mirroru lub
testów parsera.

Najpierw można wykonać bezpieczne rozpoznanie bez pobierania binariów:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Demoscene.Cli -- list --platform aga --max-items 50
dotnet run --project AmiGotekMediaBuilder.Demoscene.Cli -- list --platform aga --type demo --max-items 50
dotnet run --project AmiGotekMediaBuilder.Demoscene.Cli -- show --pouet-id 6548 --json
dotnet run --project AmiGotekMediaBuilder.Demoscene.Cli -- show --demozoo-id 396699 --json
~~~

Wynik listy jest połączonym widokiem obu katalogów. Prefix pouet: lub
demozoo: w trybie tekstowym wskazuje źródło rekordu.

Masowa synchronizacja wymaga jawnego potwierdzenia i jest wznawialna:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Demoscene.Cli -- sync `
  --platform aga --type demo --all --acknowledge-downloads `
  --max-concurrency 2 --max-bytes-total 4294967296
~~~

Parametr type=demo wymusza ten sam filtr po stronie Pouët i Demozoo.

Bez `--all` domyślnie pobieranych jest maksymalnie 100 produkcji z pierwszej
strony. Wyniki trafiają do `demoscene\downloads\`, a każdy obraz ma plik
`.source.json` z katalogiem i identyfikatorem Pouët/Demozoo, URL-em mirrora, datą i SHA-256. Metadane są zapisywane
w `demoscene\metadata\`, miniaturki w `assets\demoscene\`. Powtórne uruchomienie
pomija identyczne pliki (`AlreadyPresent`).

GUI demosceny uruchamia się jako osobny program:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Demoscene.Gui
~~~

Okno ma własne filtry platformy, typ produkcji, ekran Gotek (lista profili
480×320, 800×480 i 320×240), wyszukiwanie, miniaturki oraz przyciski
`Download selected` i `Download all results`. Pole `Local library` jest
opcjonalne: przycisk `Scan library` wyszukuje w nim istniejące ADF/DSK/ZIP, a następnie
`Export scanned` eksportuje je do wybranego katalogu. Wyszukiwanie w Pouët i
Demozoo działa bez katalogu źródłowego; eksport lokalny również dopasowuje
tytuły do katalogu demosceny i pobiera dostępne okładki. Metadane i miniaturki korzystają z
lokalnego cache'a aplikacji. Przed pobraniem lub eksportem trzeba wskazać
`Export directory`. Oba przepływy tworzą układ gotowy dla GTi i zachowują
kanoniczne nazwy TOSEC:

Po wpisaniu tytułu w polu `Search` aplikacja przeszukuje też publiczne
[archiwum scene.org](https://files.scene.org/browse/), w tym katalog party,
i dodaje kandydatów ADF/DSK, ZIP oraz GZip. scene.org dostarcza linki do plików,
ale nie metadane artworku.

~~~text
<export-directory>\ADF\Demoscene\<Release>\<nazwa dysku TOSEC>.adf
<export-directory>\DSK\Demoscene\<Release>\<nazwa dysku TOSEC>.dsk
~~~

Przykładowa nazwa wielodyskowa to
`Example Demo (Disk 1 of 2)(Data).adf`. Miniaturka demoscenowa z cache'a
jest kopiowana obok obrazu pod nazwą katalogu wydania, np. `Example Demo.jpg`.
Główne GUI
`AmiGotekMediaBuilder.Gui` nie skanuje ani nie eksportuje demosceny.

Gdy provider nie znajdzie artworku, w katalogu eksportu obok wydania zapisywana
jest wbudowana grafika dyskietki. Nie trafia ona do cache'a ani katalogów
roboczych — kolejny przebieg ponowi wyszukiwanie online. Ta sama zasada dotyczy
gier i demosceny.

Żądania demosceny nie mają sztucznego opóźnienia, dopóki host nie zasygnalizuje
przeciążenia. Kod `429` respektuje nagłówek `Retry-After`; `429`, przejściowe
błędy 5xx oraz błędy połączenia uruchamiają wspólny cooldown dla hosta i są
ponawiane maksymalnie pięć razy z wykładniczym opóźnieniem. GUI pokazuje czas
następnej próby zamiast przerywać eksport.

## Eksport Gotek

Eksport wymaga jawnego potwierdzenia gate oraz zweryfikowanych wymiarów
artworku:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- export --library-root C:\AmigaLibrary --export-gate-acknowledged --verified-artwork-width 320 --verified-artwork-height 240 --run-id review-001 --json
~~~

Jeżeli artwork ma zostać pobrany w tym samym przebiegu, dodaj do polecenia
`--online` (oraz ustaw zmienne providera jak wyżej).

Wynik trafia wyłącznie do:

~~~text
<library_root>\work\staging\<run-id>\
├── ADF\Games\<Release>\<Release> (Disk 1 of 2).adf
└── DSK\Demoscene\<Release>\<Release>.dsk
~~~

Zestawy wielodyskowe zachowują kanoniczne nazwy TOSEC, np.
`<Release> (Disk 1 of 2).adf` oraz `<Release> (Disk 2 of 2)(Data).adf`.
Obrazy specjalne zachowują rolę jako `<Release> (Save Disk).adf`, a pojedynczy
obraz pozostaje `<Release>.adf` lub `<Release>.dsk`. Zwykłe wydania trafiają
domyślnie do kategorii `Games`, a grupy demoscenowe do `Demoscene`. Obok
obrazów znajduje się NFO, artwork oraz opcjonalny manual
`assets\rtfm\<Release>.rtfm`.
Istniejące pliki o tej samej zawartości są pomijane, a konflikty zawartości
są zgłaszane i nie są nadpisywane.

Parser grupuje pliki Game (Disk 1 of 2).adf i
Game (Disk 2 of 2).adf jako jeden zestaw, a numeracja w eksporcie nie
zależy od tego, które dyski są aktualnie obecne. Format markerów wejściowych
i wyjściowych jest zgodny z [TOSEC Naming Convention](https://www.tosecdev.org/tosec-naming-convention).
Jeżeli marker `(Disk n of m)` deklaruje zestaw niekompletny, cała grupa jest
kwarantannowana: log podaje oczekiwany zakres oraz brakujące dyski, a eksport
nie tworzy ani częściowych obrazów, ani artworku. Zasada działa jednakowo dla
`Games` i `Demoscene`.

Jeśli podkatalog zawiera jedno wydanie, obrazy takie jak
`Atlantis - 01.adf` ... `Atlantis - 11.adf` trafiają do jednego katalogu
`Atlantis` i otrzymują nazwy `Atlantis (Disk 1 of 11).adf` ...
`Atlantis (Disk 11 of 11).adf`. `Atlantis - Save.adf` pozostaje obrazem
`Atlantis (Save Disk).adf` w tym samym katalogu. Jeśli jednak podkatalog zawiera
kilka różnych tytułów, jest rozpoznawany jako kolekcja i gry są eksportowane
oddzielnie. Numery dysków są odczytywane z nazwy, w tym z markerów TOSEC
`(Disk n of m)`, a nie zgadywane na podstawie kolejności plików.

Nazwa podkatalogu jest też używana jako tytuł, gdy nazwy plików są skrócone:
`FateOfAtlantis-FFAS\Atlantis - 01.adf` daje tytuł `Fate Of Atlantis`.
Jeżeli publiczny provider znajdzie tytuł kanoniczny, zastępuje tę wartość w NFO;
nazwa katalogu eksportu pozostaje bez zmian.

Tryb weryfikacyjny nie zapisuje nowych plików:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Cli -- export --library-root C:\AmigaLibrary --export-gate-acknowledged --verified-artwork-width 320 --verified-artwork-height 240 --run-id review-001 --verify-only
~~~

## GUI (Avalonia, Windows/Linux/macOS)

Uruchomienie aplikacji Avalonia z kodu źródłowego:

~~~powershell
dotnet run --project AmiGotekMediaBuilder.Gui
~~~

GUI działa na Windows, Linux i macOS; wymaga wskazania katalogu źródłowego,
a następnie prowadzi przez kolejność `Scan` → `Build` → `Export`. Po uruchomieniu
lub zmianie katalogu aktywny jest tylko `Scan`; `Build` odblokowuje się po udanym
skanowaniu, a `Export` dopiero po udanym buildzie. Katalog wejściowy jest
skanowany bezpośrednio. Jeśli konfiguracja zawiera `original_dir`, używany
jest wskazany katalog; bez niego źródłem jest `<library-root>`. Nie można
przypadkowo ustawić katalogu zapisu jako źródła, a zarządzane podkatalogi są
pomijane podczas skanowania.
Możesz też wskazać bezpośrednio istniejący katalog źródłowy, np.
`C:\AmigaLibrary\original\gry`. GUI rozpozna wtedy nadrzędny katalog biblioteki
(`C:\AmigaLibrary`) i zapisze staging, artwork, NFO oraz bazę SQLite w jego
podkatalogach `work`, `assets` i `catalog`; wybrany katalog pozostaje wyłącznie
źródłem skanowania.
Jeśli w źródle pozostały katalogi `assets`, `catalog` lub `work` utworzone przez
wcześniejszą błędną konfigurację, są rozpoznawane po markerach układu i
pomijane podczas skanowania. Nie są automatycznie usuwane.
Opcjonalnie możesz wskazać `Working directory (assets + catalog)` — katalog bazowy,
w którym GUI zapisze `assets` i `catalog` — oraz `Destination directory (staging)`
dla eksportu. Puste pola zachowują automatyczne wartości wynikające z rootu.
Wybrane ścieżki są zapamiętywane lokalnie w pliku
`%LOCALAPPDATA%\AmiGotekMediaBuilder\gui-settings.json` (Windows) lub w
`LocalApplicationData/AmiGotekMediaBuilder/gui-settings.json` na Linux/macOS.
Plik zawiera wyłącznie ścieżki, nie zawiera credentiali.
Podczas każdej operacji pasek postępu pokazuje stan pracy. Pole `Current activity`
pokazuje aktualny plik/element ZIP podczas skanowania oraz etap, tytuł i provider
przy budowaniu online. `Operation log` zawiera pełną historię kroków i błędów.
Przycisk **Cancel** obok paska postępu anuluje aktywne `Scan`, `Build` albo
`Export` w najbliższym bezpiecznym punkcie. Ukończone wcześniej pliki pozostają
zachowane, a podczas trwającej operacji nie można uruchomić drugiej.
Przed eksportem zaznacz Export gate acknowledged, wybierz ekran z listy
(profil domyślny 480×320), podaj Run ID i kliknij Export. Dostępne są profile
480×320 — Guition JC3248W535C 3.5", 800×480 — Waveshare ESP32-S3-
Touch-LCD-7 oraz 320×240 — Waveshare ESP32-S3-Touch-LCD-2.8,
zgodne z [Gotek Touchscreen interface](https://mesarim.github.io/Gotek-Touchscreen-interface/).
Wynik pojawi się pod wybranym katalogiem docelowym jako
`<destination>/<run-id>` (domyślnie `work/staging/<run-id>`). Zaznaczenie
`Online metadata + artwork` uruchamia nowy pipeline gier i pobieranie obrazków.
OpenRetro i Libretro są dostępne automatycznie, a ScreenScraper i TheGamesDB po
ustawieniu opisanych wyżej credentiali. Lokalny
GameBase jest automatycznie używany, jeśli ustawiono `AMIGA_ADF_GAMEBASE_DB`;
ścieżka nie jest już wybierana w GUI.
Przy wyłączonym online UI używa cache/fallbacku offline i nie wykonuje żądań sieciowych.
Po buildzie lista wyników GUI pokazuje zweryfikowaną miniaturkę providera, jeśli
jest dostępna. Build offline może użyć lokalnego fallbacku; w buildzie online
brak grafiki pozostaje widoczny do czasu eksportu, który dopiero wtedy zapisuje
wbudowany placeholder. Miniaturki demosceny są pomijane. Motyw głównego GUI
korzysta z granatowo-niebieskich ról kolorów inspirowanych Hall of Light;
`AmiGotekMediaBuilder.Demoscene.Gui` zachowuje własny, niezależny motyw.
GUI demosceny używa osobnego fontu MatrixType Display i zielonego motywu
matrix; jego licencja CC0 jest opisana w `THIRD_PARTY.md`.
GUI korzysta z tych samych klas Core co CLI. Publikowane wydania nie wymagają
zainstalowanego runtime .NET (zawierają go w pliku wykonywalnym).

## Wydania GitHub i pliki bez runtime

Workflow `.github/workflows/release.yml` uruchamia się wyłącznie po opublikowaniu
GitHub Release dla taga `v*` (np. `v1.1.1`). Zwykłe commity i push do gałęzi nie
uruchamiają tego workflow. Dla każdego wydania buduje główne CLI/GUI oraz
CLI/GUI demosceny jako self-contained, single-file i dołącza je do tego Release.
Używane są desktopowe identyfikatory RID z [katalogu RID .NET](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog):
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` i `osx-arm64`.

Nazwy assetów mają postać:

~~~text
ami-gotek-media-builder-cli-v1.1.1-win-x64.zip
ami-gotek-media-builder-gui-v1.1.1-win-x64.zip
ami-gotek-media-builder-demoscene-cli-v1.1.1-win-x64.zip
ami-gotek-media-builder-demoscene-gui-v1.1.1-win-x64.zip
~~~

Analogiczne pliki są tworzone dla pozostałych RID-ów. ZIP zawiera gotowy
program (`AmiGotekMediaBuilder.Cli`, `AmiGotekMediaBuilder.Gui` albo program
demoscenowy), więc na komputerze docelowym nie trzeba instalować .NET. Tag musi
zaczynać się od `v`, a token workflowa ma uprawnienie `contents: write`, aby
załączyć assety do Release. Workflow używa wersji akcji GitHub zgodnych z
Node.js 24.

Przykładowe wydanie z taga — po wypchnięciu taga utwórz i opublikuj Release:

~~~powershell
git tag v1.1.1
git push origin v1.1.1
gh release create v1.1.1 --generate-notes --title v1.1.1
~~~

Po opublikowaniu Release workflow uruchomi się raz i dołączy do niego assety.

## Bezpieczeństwo

- obrazy wejściowe nie są modyfikowane; przy skanowaniu rootu katalogi zarządzane
  są pomijane;
- katalog stagingu jest izolowany przez run-id;
- ścieżki są normalizowane i sprawdzane pod kątem containment/symlinków;
- eksport nie zapisuje bezpośrednio na kartę SD;
- HTTP jest domyślnie nieużywany; providerzy blokują prywatne adresy,
  niekontrolowane redirecty i zbyt duże odpowiedzi (downloader ma limitowane,
  walidowane przekierowania);
- NFO jest ograniczone do 512 bajtów UTF-8;
- źródła nie są usuwane ani przenoszone przez aplikację.

## Aktualny zakres

Gotowe są: offline/online Core, CLI, katalog, eksport z artworkiem, NFO, cache
metadanych, pipeline GameBase/ScreenScraper/OpenRetro/TheGamesDB/Libretro oraz providerzy
Pouët/Demozoo dla demosceny,
masowy downloader demosceny z deduplikacją, walidacja ścieżek, bezpieczny
transport HTTP i wieloplatformowe GUI Avalonia.

Poza zakresem pozostają konwersja DMS/LHA do ADF, artwork/PDF OCR, ręczne
approvals oraz pełna zgodność z każdą funkcją aplikacji Python.
