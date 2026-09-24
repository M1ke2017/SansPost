using SansPost.Features.Posts;

namespace SansPost.Features.Demo
{
    // Treści startowe publicznego demo — zapisywane w bazie przez DemoContentSeeder (nie w Razor).
    // Autorzy używają tego samego systemu przydomków co zwykłe konta. Emaile w zarezerwowanej domenie .invalid —
    // nie wyglądają jak dane realnych osób i nigdzie nie są wyświetlane.
    public static class DemoContent
    {
        public const string EmailDomain = "demo.sanspost.invalid";
        public const string WelcomeTitle = "Witaj w SansPost";

        public static bool IsDemoEmail(string? email) =>
            email is not null && email.Trim().EndsWith("@" + EmailDomain, StringComparison.OrdinalIgnoreCase);

        public static readonly IReadOnlyList<string> Authors = new[]
        {
            "DustyRaven", "CoyoteRose", "CopperJack", "RedMesa", "SageWalker", "LonePine", "CanyonFox", "MidnightTrail"
        };

        public sealed record DemoComment(int Author, string Content, double HoursAfterPost);

        // Author = indeks w Authors (-1 = post powitalny: administrator, jeśli istnieje, inaczej pierwszy autor demo).
        public sealed record DemoPost(int Author, PostCategory Category, string Title, string Content, double AgeHours,
            IReadOnlyList<DemoComment> Comments, IReadOnlyList<int> LikedBy);

        public static readonly IReadOnlyList<DemoPost> Posts = new DemoPost[]
        {
            new(-1, PostCategory.Feedback, WelcomeTitle,
                "To działający projekt portfolio: publiczna przestrzeń rozmów o technologii, grach, podróżach, pomysłach i projektach.\n\n" +
                "Rozejrzyj się, załóż konto — dostaniesz własny przydomek — dołącz do dyskusji i zostaw opinię w kategorii Feedback. Każda uwaga trafia do dalszej pracy.",
                AgeHours: 24 * 14,
                new DemoComment[] { new(1, "Przydomek dostałam od razu i nawet mi się podoba. Dobry pomysł zamiast wymyślania nicku.", 30) },
                new[] { 0, 1, 2, 4, 6 }),

            new(5, PostCategory.General, "Jaka mała rzecz najbardziej poprawiła Twój codzienny dzień?",
                "U mnie to był zwykły termos. Kawa wypita o dziesiątej, a nie wystygła o ósmej piętnaście.\n\nCiekawi mnie, jakie drobiazgi zmieniły coś u Was.",
                AgeHours: 5,
                new DemoComment[]
                {
                    new(3, "Lampka z ciepłym światłem przy biurku. Wieczorem przestałam mieć wrażenie, że siedzę w biurze.", 1),
                    new(0, "Stałe miejsce na klucze. Brzmi banalnie, ale oszczędza mi nerwy każdego ranka.", 2),
                    new(6, "Spacer bez telefonu po obiedzie. Dziesięć minut, a głowa zupełnie inna.", 3.5)
                },
                new[] { 0, 3, 6 }),

            new(2, PostCategory.Technology, "Które narzędzie naprawdę zmieniło sposób, w jaki pracujesz?",
                "Nie pytam o modne nowości, tylko o coś, bez czego nie wyobrażacie sobie już pracy.\n\nU mnie: porządny menedżer schowka i skróty klawiszowe do układania okien.",
                AgeHours: 20,
                new DemoComment[]
                {
                    new(4, "Menedżer haseł. Przestałem wymyślać i zapominać — i przestałem się bać zmiany hasła.", 1),
                    new(7, "Terminal z historią wyszukiwaną jak w przeglądarce. Wstyd, ile lat tego nie używałem.", 3),
                    new(0, "Kalendarz z blokami na skupienie. Narzędzie proste, zmiana ogromna.", 6),
                    new(2, "Blok na skupienie to dobry trop. Też zaczynam od tego poniedziałku.", 7)
                },
                new[] { 0, 3, 4, 7 }),

            new(6, PostCategory.Games, "Która gra stworzyła najbardziej wiarygodny świat?",
                "Chodzi mi o świat, w który się wierzy: mieszkańcy mają rutyny, miejsca mają historię, a mapa nie wygląda jak lista zadań.\n\nMoje typy to Outer Wilds i pierwszy Gothic.",
                AgeHours: 30,
                new DemoComment[]
                {
                    new(2, "Red Dead Redemption 2 — obozowisko żyje nawet wtedy, gdy nic nie robisz.", 2),
                    new(5, "Disco Elysium. Świat zbudowany prawie wyłącznie z rozmów, a czuć go lepiej niż niejedną mapę 3D.", 4),
                    new(6, "Disco Elysium mam na liście od dawna. Chyba czas.", 5)
                },
                new[] { 2, 5, 7 }),

            new(4, PostCategory.Travel, "Wolisz planować podróż dokładnie czy improwizować?",
                "Ja mam zawsze plan na pierwszy i ostatni dzień, a środek zostawiam otwarty.\n\nNajlepsze miejsca znalazłem przez przypadek, ale nocleg na start wolę mieć pewny.",
                AgeHours: 44,
                new DemoComment[]
                {
                    new(1, "Tak samo! Plan to rusztowanie, nie więzienie.", 2),
                    new(6, "Ja improwizuję do czasu pierwszej nocy na dworcu. Potem nagle lubię plany.", 5)
                },
                new[] { 1, 6 }),

            new(3, PostCategory.Ideas, "Jaki prosty pomysł na aplikację naprawdę rozwiązuje realny problem?",
                "Nie kolejna aplikacja do notatek. Coś małego, co oszczędza dziesięć minut dziennie.\n\nMój kandydat: wspólna lista zakupów, która sama układa produkty według alejek w sklepie.",
                AgeHours: 60,
                new DemoComment[]
                {
                    new(2, "Przypomnienie o wymianie filtrów, baterii i terminach przeglądów — w jednym miejscu, bez kont i reklam.", 3),
                    new(7, "Lista zakupów po alejkach to złoto. Tylko każdy sklep ma inny układ…", 5),
                    new(3, "Dlatego pierwsza wersja uczyłaby się z kolejności odhaczania.", 6)
                },
                new[] { 2, 7 }),

            new(7, PostCategory.Projects, "Co sprawia, że projekt portfolio chce się faktycznie otworzyć?",
                "Przeglądam sporo repozytoriów. Zatrzymuje mnie działające demo, krótki opis problemu i jedno zdanie o tym, co było naprawdę trudne.\n\nCo działa na Was?",
                AgeHours: 72,
                new DemoComment[]
                {
                    new(0, "Zrzut ekranu w README. Zanim cokolwiek sklonuję, chcę zobaczyć, co to jest.", 2),
                    new(4, "Sekcja \"czego nie zrobiłem i dlaczego\". Uczciwość robi lepsze wrażenie niż lista technologii.", 4),
                    new(1, "Demo, które nie wymaga zakładania konta, żeby się rozejrzeć.", 9)
                },
                new[] { 0, 1, 4 }),

            new(1, PostCategory.Feedback, "Co jako pierwsze zmieniłbyś w SansPost?",
                "Zbieramy uwagi po pierwszych dniach. Czego brakuje, co przeszkadza, a co działa lepiej, niż się spodziewaliście?",
                AgeHours: 96,
                new DemoComment[]
                {
                    new(5, "Chętnie zobaczyłbym powiadomienia o odpowiedziach pod moimi postami.", 3),
                    new(6, "Ciemny motyw jest bardzo przyjemny. Na razie nic bym nie ruszał.", 8)
                },
                new[] { 5 }),

            new(0, PostCategory.General, "Najlepsza rzecz zjedzona w podróży służbowej",
                "Budka z pierogami przy dworcu w Przemyślu. Do dziś żałuję, że wziąłem tylko jedną porcję.",
                AgeHours: 110,
                new DemoComment[] { new(3, "Takie miejsca zawsze są przy dworcach. Nigdy w przewodnikach.", 6) },
                new[] { 3, 5 }),

            new(4, PostCategory.Technology, "Ciemny motyw wieczorem — naprawdę pomaga?",
                "Mam wrażenie, że bardziej pomaga mniejsza jasność ekranu niż sam ciemny motyw.\n\nKtoś sprawdzał to na sobie dłużej?",
                AgeHours: 130,
                new DemoComment[]
                {
                    new(7, "U mnie największą różnicę robi ciepła barwa ekranu po zmroku.", 2),
                    new(2, "Ciemny motyw lubię za mniejszy kontrast wokół tekstu, nie za sen.", 10)
                },
                Array.Empty<int>()),

            new(5, PostCategory.Games, "Planszówka na dwie osoby, która nie trwa trzech godzin",
                "Szukam czegoś na wieczór po pracy: 30–45 minut, sporo decyzji, mało losowości.",
                AgeHours: 160,
                new DemoComment[]
                {
                    new(6, "Patchwork. Krótki, bardzo decyzyjny i świetnie się tłumaczy.", 2),
                    new(1, "Jaipur — handel, trochę blefu i zawsze chce się rewanżu.", 12)
                },
                new[] { 6 }),

            new(6, PostCategory.Travel, "Trzy dni w górach bez samochodu",
                "Planuję trasę pociąg, autobus i pieszo. Jak realnie sprawdzić, czy lokalne kursy nie znikną poza sezonem?",
                AgeHours: 190,
                new DemoComment[] { new(4, "Dzwoniłbym do informacji turystycznej. Rozkłady online bywają sprzed dwóch lat.", 5) },
                Array.Empty<int>()),

            new(2, PostCategory.Ideas, "Tablica ogłoszeń dla jednej kamienicy",
                "Coś pomiędzy grupą na komunikatorze a kartką na drzwiach: zgubione klucze, pożyczę wiertarkę, awaria wody.\n\nBez zakładania kont — wystarczy kod z klatki schodowej.",
                AgeHours: 220,
                new DemoComment[]
                {
                    new(0, "Byłbym pierwszym użytkownikiem. Kartki na drzwiach giną po dwóch dniach.", 4),
                    new(3, "Warto dodać wygasanie ogłoszeń, żeby tablica nie zamieniła się w archiwum.", 9)
                },
                new[] { 0, 3 }),

            new(0, PostCategory.Projects, "Mały projekt: stacja pogodowa na balkonie",
                "ESP32, czujnik temperatury i wilgotności, wykres w przeglądarce.\n\nNajwięcej czasu zajęło mi… zabezpieczenie obudowy przed deszczem.",
                AgeHours: 260,
                new DemoComment[]
                {
                    new(7, "Klasyka — elektronika działa w godzinę, obudowa zajmuje tydzień.", 3),
                    new(2, "Pokażesz wykres? Ciekawi mnie, jak szybko reaguje czujnik wilgotności.", 20)
                },
                new[] { 2, 4, 7 }),

            new(3, PostCategory.General, "Książka, do której wracacie co kilka lat",
                "Moja to „Mały Książę”. Za każdym razem czytam ją jak zupełnie inną książkę.",
                AgeHours: 300,
                Array.Empty<DemoComment>(),
                new[] { 1 }),

            new(7, PostCategory.Games, "Gry, które najlepiej wyglądają w ciemnym pokoju",
                "Szukam tytułów z klimatycznym światłem — latarnie, ogniska, neon deszczowego miasta. Co polecacie na długi wieczór?",
                AgeHours: 340,
                Array.Empty<DemoComment>(),
                Array.Empty<int>())
        };
    }
}
