# Globalny skrót Revert

Domyślna kombinacja: **Ctrl+Alt+Shift+R**. Działa w sesji użytkownika,
również przy aplikacji ukrytej w trayu, dopóki DesktopSplitter jest uruchomiony
i jego dispatcher obsługuje komunikaty. Nie zastępuje niezależnego watchdoga
na wypadek zawieszenia procesu ani skrótu obsługiwanego na secure desktop.

## Dlaczego nie domyślnie Win+Esc?

Windows używa Win+Esc do wyłączania Lupy:
https://support.microsoft.com/en-us/accessibility/windows/magnifier/magnifier-keyboard-shortcuts-and-touch-gestures

Microsoft rezerwuje kombinacje z klawiszem Windows dla systemu:
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey

Można wpisać Win+Esc w ustawieniach, ale nie jest to gwarantowany wolny ani
niezawodny skrót aplikacji. Program używa RegisterHotKey, nie przechwytuje go
hookiem i nie wyłącza skrótów systemowych. Nawet kombinację Ctrl+Alt+Shift+R
może wcześniej zarejestrować inna aplikacja; wynik rejestracji jest sprawdzany.
Dotychczasowy skrót Compositora Ctrl+Alt+Shift+S nadal oznacza suspend/resume.

## Ustawienia

W głównym oknie, w sekcji **GLOBAL REVERT SHORTCUT**:

1. Zaznacz Enable global Revert shortcut.
2. Wpisz kombinację, np. Ctrl+Alt+Shift+R, Ctrl+Alt+Q albo Ctrl+Shift+F11.
3. Wybierz Save shortcut i sprawdź komunikat pokazujący rzeczywiście aktywny skrót.

Obsługiwane są modyfikatory Ctrl, Alt, Shift, Win oraz klawisze A–Z, 0–9,
Esc i F1–F24. F12 i Ctrl+Shift+Esc są jawnie odrzucane jako zarezerwowane.
Wymagany jest przynajmniej jeden modyfikator. Odznaczenie checkboxa i zapis
wyrejestrowują skrót. Ustawienia są przechowywane w istniejącym pliku
`%APPDATA%\DesktopSplitter\settings.json` jako `globalRevertHotkeyEnabled`
i `globalRevertHotkey`; Apply zachowuje te pola. Starsze pliki otrzymują
wartość domyślną bez utraty istniejących ustawień i kopii migracyjnej.

Przy zmianie program rejestruje nową kombinację przed zwolnieniem poprzedniej.
Konflikt z inną aplikacją albo błąd zapisu nie usuwają poprzedniego działającego
skrótu i nie zapisują nieaktywnej kombinacji jako działającej. Status pokazuje
faktycznie aktywną kombinację także po błędzie. Przy starcie błąd rejestracji
jest zgłaszany; ustawienia nie są automatycznie nadpisywane innym skrótem.

## Zachowanie Revert

WM_HOTKEY jest odbierane przez osobne niewidoczne okno komunikatów, niezależnie
od pokazywania głównego okna. MOD_NOREPEAT ogranicza powtarzanie przy trzymaniu
klawiszy; dodatkowo równoległe żądania Revert są łączone.

Podczas Apply żądanie Revert ustawia pojedyncze oczekujące żądanie i anuluje
token Apply. Revert uruchamia się, gdy Apply opuści swoją ścieżkę operacji,
przed odświeżaniem UI. Anulowanie jest kooperacyjne: trwające wywołanie Windows
może najpierw wymagać zakończenia. Żądanie nie uruchamia równolegle drugiej
rekonfiguracji. Obowiązują istniejące kontrole trwałej tożsamości monitorów;
przy niepewnym dopasowaniu program zachowuje dane odzyskiwania i zgłasza błąd.

## Testy

Zestaw `tests/MonitorIdentity` obejmuje dodatkowo: parser i normalizację,
nieprawidłowe kombinacje, konflikt rejestracji, wycofanie po błędzie zapisu,
przełączenie i odrzucanie starych komunikatów, wyłączenie i cleanup,
odtworzenie ustawień oraz kolejkę Revert podczas Apply. Testy korzystają
z atrap backendu i nie zmieniają konfiguracji ekranów ani rejestru.

Na Windows 11 należy ręcznie sprawdzić:

- domyślny skrót przy aktywnym oknie innej aplikacji i DesktopSplitter w trayu;
- zapis innej kombinacji, restart aplikacji i zwolnienie skrótu po wyjściu;
- konflikt z drugą instancją/rejestracją: poprzedni skrót pozostaje aktywny;
- brak wielu równoległych Revert po wielokrotnym naciśnięciu;
- żądanie podczas Apply i odliczania oraz przywrócenie specialized monitora;
- obsługę skrótu w konkretnej konfiguracji RDP, ponieważ skróty mogą być
  kierowane do sesji lokalnej albo zdalnej przez klienta RDP.
