<p align="center"><img src="docs/images/icon.png" alt="Ikona PoGGaduchów: kontur białej słuchawki na niebieskim tle" width="112"></p>

# PoGGaduchy

**Prosty program do rozmów wideo dla rodziny – dla osób starszych i słabowidzących (Windows 10/11).**

Duże, kontrastowe kafelki z osobami i wskaźnikiem „jest online”. Jedno kliknięcie wybiera osobę, drugie (wielki zielony
przycisk z kamerką) dzwoni. Rozmowa przychodzi na cały ekran z wielkim zielonym „Odbierz” i czerwonym „Odrzuć”. Podczas
rozmowy w prawym dolnym rogu świeci duża czerwona słuchawka. Rozmowy toczą się **bezpośrednio między komputerami**
(WebRTC, szyfrowane), a serwer w chmurze tylko łączy ze sobą rodzinę – i nie widzi ani obrazu, ani dźwięku.

> ### 🤖 Program powstaje z intensywnym udziałem AI
> Ten projekt jest tworzony we współpracy z asystentem AI (**Claude**, Anthropic). AI napisało większość kodu, tekstów
> interfejsu i tę dokumentację, na podstawie wymagań i uwag właściciela projektu, który kierował pomysłem i sprawdzał
> program w działaniu. Kod miał **ograniczony przegląd przez człowieka** i był testowany głównie na **Windows 11**. Przejrzyj
> go, zanim zaczniesz na nim polegać. Zgłoszenia błędów i propozycje zmian są mile widziane.

| Lista osób | Rozmowa przychodzi | Rozmowa |
|---|---|---|
| ![Lista](docs/images/main.png) | ![Dzwoni](docs/images/incoming.png) | ![Rozmowa](docs/images/call.png) |

*(Zrzut rozmowy pokazuje sztuczną kamerę testową.)*

## Funkcje

* **Kafelki osób** po lewej: zdjęcie po lewej stronie kafelka, nazwa po prawej, kolorowa kropka i napis „jest online”,
  „rozmawia” albo „nie ma w sieci”. Szerokość, wysokość kafelka i wielkość czcionki ustawia się suwakami.
* **Wybrana osoba**: na środku u góry duże zdjęcie z podpisem, pod spodem zielony przycisk z kamerką i słuchawką.
* **Rozmowa wideo jednym przyciskiem.** Okno rozmowy jest zmaksymalizowane, a w prawym dolnym rogu świeci wielka czerwona
  słuchawka, która kończy rozmowę. Mały podgląd własnego obrazu jest w lewym dolnym rogu.
* **Rozmowa przychodząca**: pełnoekranowy komunikat z dużym zdjęciem i nazwą osoby, dzwonek, wielki zielony „Odbierz”
  i czerwony „Odrzuć” (z klawiatury: Enter / Spacja – odbierz, Esc – odrzuć).
* **Automatyczne odbieranie** od wskazanych, zaufanych osób (Ustawienia → osoba → „Odbieraj rozmowy od tej osoby automatycznie”).
* **Brak kamery**: duża informacja „Nie wykryto kamery” i przycisk „Zadzwoń bez kamery” (rozmowa tylko z dźwiękiem;
  drugą osobę nadal widać na ekranie). Brak mikrofonu też nie przerywa rozmowy (słychać drugą osobę).
* **Samo ponawia nieudane kroki**: połączenie z serwerem jest stale podtrzymywane i wznawiane po zerwaniu, po powrocie
  sieci i po wybudzeniu komputera; dzwonienie czeka na połączenie z serwerem; zerwane połączenie obrazu jest odtwarzane
  (ponowne uzgodnienie ścieżki sieciowej) przez minutę, zanim program zakończy rozmowę.
* **Kamera i mikrofon bez pytań**: zgoda jest wbudowana w program i dotyczy tylko jego strony rozmowy.
* **Własne nazwy i zdjęcia** osób (niezależne od nazw na serwerze): zdjęcie można **przesuwać myszą, powiększać,
  pomniejszać i obracać**. Osobę można też ukryć na liście.
* **Bezpieczne ustawienia**: przycisk „Ustawienia” (na dole po prawej) trzeba **przytrzymać przez 1 sekundę**. Krótkie
  kliknięcie pokazuje na 3 sekundy podpowiedź. Czerwony przycisk **X** (128 × 128) w prawym górnym rogu wyłącza program.
* Zawsze uruchamia się **zmaksymalizowany**; **startuje razem z Windows**. Druga uruchomiona kopia nie otwiera nowej,
  tylko wysuwa pierwszą (a gdy trwa rozmowa – okno rozmowy).
* **Automatyczne aktualizacje** z GitHub Releases, także **ciche** (bez pytania) – obie funkcje można wyłączyć w Ustawieniach.
* Dane (ustawienia, zdjęcia, log) leżą **obok pliku `.exe`**.

## Jak to działa

```
 komputer A (PoGGaduchy.exe)                                  komputer B (PoGGaduchy.exe)
   ├─ WPF + WebView2 (WebRTC) ◄──────── obraz i dźwięk, szyfrowane (P2P lub przez TURN) ────────► ...
   └─ WebSocket ─► Cloudflare Worker + Durable Object (obecność, „X dzwoni do Y”, dane negocjacji) ◄─ WebSocket
                                  └─ wydaje krótko ważne poświadczenia TURN (Cloudflare Realtime TURN)
```

* **Serwer** (katalog [`server/`](server)) to jeden Cloudflare Worker z jednym Durable Object i połączeniami WebSocket w trybie
  hibernacji. Przekazuje tylko: kto jest online, „dzwoni”, „odebrał / odrzucił / rozłączył” i dane negocjacji połączenia.
  Podtrzymanie połączenia to ramki `ping`/`pong` obsługiwane automatycznie przez platformę (bez budzenia obiektu i bez
  zużywania limitu); zerwane połączenia są usuwane z listy „online” po ok. 100 s.
* **Dostęp**: każda kopia programu ma długi, losowy, tajny klucz (256 bitów). Serwer zna tylko **skróty SHA-256** dozwolonych
  kluczy. Obca osoba bez klucza nie może ani się zalogować, ani zadzwonić do rodziny; błędne próby z jednego adresu IP są
  ograniczane (po 10 w minucie – odpowiedź 429), a ramki z jednego połączenia mają limit szybkości i rozmiaru.
* **TURN** (jeśli skonfigurowany; patrz niżej): gdy bezpośrednie połączenie nie jest możliwe (np. za zaporą), obraz i dźwięk idą przez Cloudflare Realtime TURN.
  Dane uwierzytelniające wydaje serwer i ważne są tylko przez ograniczony czas (domyślnie 6 godzin).
* **Limity darmowego planu Cloudflare** (m.in. 100 000 żądań dziennie) wystarczają dla ok. 10 osób: program nie wysyła
  „pingów” co sekundę, a podtrzymanie połączenia idzie przez auto-odpowiedź.

## Instalacja i konfiguracja (dla osoby, która przygotowuje program dla rodziny)

### 1. Serwer na Cloudflare (jednorazowo, darmowe konto)

Potrzebny jest Node.js tylko do wdrożenia serwera (do samego programu nie jest potrzebny). Wystarczy **przenośny** Node
(zip z nodejs.org rozpakowany do `%USERPROFILE%\tools\`) – skrypty same go znajdują (albo ustaw zmienną `PGG_NODE`).

```powershell
cd server
npm install                                  # pobiera wranglera (narzędzie Cloudflare)
.\node_modules\.bin\wrangler login           # logowanie do Cloudflare w przeglądarce
cd ..
.\tools\wdroz.ps1                            # wdraża serwer i zapisuje jego adres (*.workers.dev)
```

**TURN (opcjonalnie, ale zalecane)**: bez niego program działa na samych serwerach STUN, co wystarcza w większości domowych
sieci, ale rozmowa nie połączy się, gdy któraś osoba jest za „trudnym” routerem (CGNAT u operatora, sieć firmowa).
Cloudflare wymaga do włączenia Realtime podania metody płatności (pierwsze 1000 GB miesięcznie jest bezpłatne; płatne jest
dopiero to, co ponad limit). Po utworzeniu klucza (panel Cloudflare: Realtime → TURN Server → Create) wpisz jego dane
skryptem (wartości wpisujesz samodzielnie, skrypt ich nie wyświetla i nie zapisuje w plikach):

```powershell
.\tools\ustaw-turn.ps1
```

### 2. Osoby (rodzina)

```powershell
.\tools\dodaj-osobe.ps1 -Id mama -Nazwa "Mama" -Serwer https://poggaduchy.twoje-konto.workers.dev -Wyslij
```

Skrypt tworzy klucz osoby, zapisuje **skrót** klucza na liście serwera (`server\members.json`, poza repozytorium)
i tworzy plik `instalacje\mama\kod-instalacji.txt`. Jeśli nie podasz `-Wyslij`, listę wysyłasz później:
`.\tools\wyslij-osoby.ps1`. Nowy klucz dla istniejącej osoby: `-NowyKlucz`; usunięcie osoby: `-Usun`.
Pliki `kod-instalacji.txt` zawierają **tajne klucze** – przekazuj je tylko właściwym osobom.

### 3. Program na komputerze osoby

```powershell
.\tools\przygotuj-kopie.ps1 -Id mama      # składa folder i zip: PoGGaduchy.exe + kod-instalacji.txt
```

Na komputerze osoby: rozpakuj zip w dowolnym folderze i uruchom `PoGGaduchy.exe` (nie wymaga instalowania .NET). Program
sam wpisze się do autostartu Windows. Osoby z serwera pojawiają się na kafelkach automatycznie; nazwy i zdjęcia
ustawia się raz w Ustawieniach (przytrzymaj przycisk 1 sekundę).

Wymagania: Windows 10/11 (64-bit) ze składnikiem **Microsoft Edge WebView2** (jest w Windows 11 i w aktualnym Windows 10).

## Aktualizacje

Program co kilka godzin pyta GitHub o najnowsze wydanie repozytorium zapisanego w `Source/Models.cs`
(`AppInfo.DefaultRepository`). Nowsza wersja jest pobierana w tle, a jej suma **SHA-256** jest sprawdzana (z pliku
`.sha256` wydania lub z sumy podanej przez GitHub; bez sumy aktualizacja nie jest instalowana). Plik musi pochodzić z
`github.com` / `githubusercontent.com` (HTTPS) i być programem (`MZ`).

W Ustawieniach są dwie niezależne opcje:

* **Sprawdzaj i pobieraj aktualizacje automatycznie** – wyłączenie zatrzymuje sprawdzanie (zostaje przycisk „Sprawdź teraz”).
* **Cicha aktualizacja** – pobrana wersja jest instalowana bez pytania, **gdy nikt nie korzysta z programu** (5 minut bez ruchu
  myszą, nie trwa rozmowa) albo **przy uruchomieniu**. Po wyłączeniu na ekranie pojawia się duży przycisk „Zainstaluj nową wersję
  programu”.

### Jak wydać nową wersję

```bash
git tag v1.2.0
git push origin v1.2.0
```

Workflow [`release.yml`](.github/workflows/release.yml) zbuduje program (numer wersji bierze z tagu), spakuje do
`PoGGaduchy-1.2.0-win-x64.zip`, doda sumę `.sha256` i opublikuje wydanie (z informacją o udziale AI w opisie).

## Gdzie są ustawienia

**Obok pliku `PoGGaduchy.exe`**: `settings.json` (osoby, rozmiary, opcje), `kod-instalacji.txt` (tożsamość tej kopii),
folder `images` (zdjęcia), `webview2` (dane przeglądarki wbudowanej w rozmowę) i `log.txt`. Cały folder można skopiować
na inny komputer (uwaga: ten sam kod instalacji na dwóch komputerach naraz – drugi wypiera pierwszy). Jeśli folder
programu jest tylko do odczytu, dane trafiają do `%APPDATA%\PoGGaduchy`.

## Diagnostyka

`PoGGaduchy.exe --diagnoza` (tylko odczyt, nic nie zmienia i nie wypiera działającego programu) zapisuje w `log.txt`:
wersję, WebView2, kamery i mikrofony, zgody Windows na kamerę i mikrofon, autostart, kod instalacji oraz odpowiedź
serwera (w tym, czy działa TURN).

## Budowanie z kodu i testy

Wymagany [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) na Windows.

```bash
cd Source
dotnet build          # wersja debug
cd ..
build.cmd             # samodzielny PoGGaduchy.exe w tym folderze
```

Testy (opisy w nagłówkach skryptów):

* `server/test/protocol.mjs` – protokół serwera (logowanie, obecność, dzwonienie, odrzucanie, wznawianie, TURN…),
  `server/test/stale.mjs` – sprzątanie zerwanych połączeń. Uruchamiane na lokalnym serwerze (`npm run dev`).
* `tools/test-call.ps1` – dwie kopie programu na jednym komputerze (osobne foldery danych) dzwonią do siebie przez serwer,
  sterowane przez UI Automation i mysz: odebranie, odrzucenie, rezygnacja, brak kamery, odbieranie automatyczne,
  opcjonalnie wymuszony przekaźnik TURN (`-ForceRelay`).
* `tools/test-ui.ps1` – przytrzymanie przycisku ustawień, podpowiedź po krótkim kliknięciu, przycisk X.
* `tools/shots.ps1` – zrzuty okien dla różnych rozmiarów ekranu (układ sprawdzany m.in. dla 1280×720, 1536×864, 1920×1080
  i 3440×1440 przy skalowaniu Windows 100–150%).

Zmienne środowiskowe tylko do testów: `PGG_DATA` (inny folder danych), `PGG_FAKE_MEDIA=1` (sztuczna kamera i mikrofon),
`PGG_NO_CAMERA=1`, `PGG_FORCE_RELAY=1`, `PGG_NO_UPDATE=1`, `PGG_REPO` (inne repozytorium aktualizacji).

## Znane ograniczenia

* Program musi być uruchomiony, a komputer nie może być uśpiony, żeby dało się do niego zadzwonić.
* **Bez skonfigurowanego TURN** (stan domyślny, dopóki nie wykonasz kroku z `ustaw-turn.ps1`) rozmowa łączy się tylko wtedy,
  gdy da się ją nawiązać bezpośrednio między komputerami. Ścieżka przez przekaźnik TURN jest zaimplementowana, ale
  **nie była jeszcze sprawdzona w praktyce**.
* Zapora Windows przy pierwszym połączeniu może zapytać o zgodę dla składnika `msedgewebview2.exe` (nie sprawdzano na
  świeżym systemie).
* Brak składnika WebView2 (rzadko, na starym Windows 10) uniemożliwia rozmowy; program pokazuje wtedy komunikat.
* Tylko rozmowy jeden na jeden, bez czatu tekstowego i bez aplikacji mobilnej.

## Licencja

[MIT](LICENSE) © NepciorLab
