namespace SansPost.Shared.Ui
{
    // Ścieżki SVG w stylu liniowym (siatka 24×24). Stałe, zaufane dane — nigdy treść użytkownika.
    public static class Icons
    {
        public const string Search = "search";
        public const string Compass = "compass";
        public const string Grid = "grid";
        public const string Plus = "plus";
        public const string PenSquare = "pen-square";
        public const string Pencil = "pencil";
        public const string User = "user";
        public const string Users = "users";
        public const string LogOut = "log-out";
        public const string LogIn = "log-in";
        public const string Sun = "sun";
        public const string Moon = "moon";
        public const string Heart = "heart";
        public const string Comment = "comment";
        public const string Flag = "flag";
        public const string Trash = "trash";
        public const string X = "x";
        public const string ChevronDown = "chevron-down";
        public const string ArrowLeft = "arrow-left";
        public const string Refresh = "refresh";
        public const string AlertTriangle = "alert-triangle";
        public const string AlertCircle = "alert-circle";
        public const string CheckCircle = "check-circle";
        public const string Info = "info";
        public const string Lock = "lock";
        public const string Clock = "clock";
        public const string Eye = "eye";
        public const string EyeOff = "eye-off";
        public const string Shield = "shield";
        public const string Ban = "ban";
        public const string UserCheck = "user-check";
        public const string Pause = "pause";
        public const string Inbox = "inbox";
        public const string Trending = "trending";
        public const string FileText = "file-text";
        public const string Calendar = "calendar";
        public const string MessageSquare = "message-square";
        public const string Cpu = "cpu";
        public const string Gamepad = "gamepad";
        public const string Map = "map";
        public const string Lightbulb = "lightbulb";
        public const string Wrench = "wrench";
        public const string Megaphone = "megaphone";
        public const string Pin = "pin";
        public const string Menu = "menu";

        // Frontier: symbole kategorii i akcji publikacji (ten sam styl liniowy co reszta zestawu).
        public const string SaloonSign = "saloon-sign";
        public const string Telegraph = "telegraph";
        public const string Cards = "cards";
        public const string Signpost = "signpost";
        public const string Lantern = "lantern";
        public const string Anvil = "anvil";
        public const string NoticeBoard = "notice-board";
        public const string Cactus = "cactus";
        public const string Bell = "bell";

        // Karty komend "Śladem Rewolwerowca" (Sprint 19): strzał, unik, przeładowanie (bęben).
        public const string Revolver = "revolver";
        public const string Swerve = "swerve";
        public const string Cylinder = "cylinder";

        private static readonly Dictionary<string, string> Paths = new()
        {
            [Search] = "<circle cx='11' cy='11' r='7.5'/><path d='m20.5 20.5-4.2-4.2'/>",
            [Compass] = "<circle cx='12' cy='12' r='9.5'/><path d='m15.8 8.2-2 5.6-5.6 2 2-5.6z'/>",
            [Grid] = "<rect x='3.5' y='3.5' width='7' height='7' rx='1.5'/><rect x='13.5' y='3.5' width='7' height='7' rx='1.5'/><rect x='13.5' y='13.5' width='7' height='7' rx='1.5'/><rect x='3.5' y='13.5' width='7' height='7' rx='1.5'/>",
            [Plus] = "<path d='M5 12h14'/><path d='M12 5v14'/>",
            [PenSquare] = "<path d='M12 3.5H5.5a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h13a2 2 0 0 0 2-2V12'/><path d='M18.4 2.6a2.1 2.1 0 0 1 3 3L12 15l-4 1 1-4z'/>",
            [Pencil] = "<path d='M17 3a2.8 2.8 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5z'/><path d='m15 5 4 4'/>",
            [User] = "<circle cx='12' cy='8' r='4.5'/><path d='M20 21a8 8 0 0 0-16 0'/>",
            [Users] = "<path d='M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M22 21v-2a4 4 0 0 0-3-3.9'/><path d='M16 3.1a4 4 0 0 1 0 7.8'/>",
            [LogOut] = "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4'/><path d='m16 17 5-5-5-5'/><path d='M21 12H9'/>",
            [LogIn] = "<path d='M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4'/><path d='m10 17 5-5-5-5'/><path d='M15 12H3'/>",
            [Sun] = "<circle cx='12' cy='12' r='4'/><path d='M12 2v2'/><path d='M12 20v2'/><path d='m4.9 4.9 1.4 1.4'/><path d='m17.7 17.7 1.4 1.4'/><path d='M2 12h2'/><path d='M20 12h2'/><path d='m6.3 17.7-1.4 1.4'/><path d='m19.1 4.9-1.4 1.4'/>",
            [Moon] = "<path d='M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9z'/>",
            [Heart] = "<path d='M19 14c1.5-1.5 3-3.2 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.8 0-3 .5-4.5 2-1.5-1.5-2.7-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4 3 5.5l7 7z'/>",
            [Comment] = "<path d='M7.9 20A9 9 0 1 0 4 16.1L2 22z'/>",
            [Flag] = "<path d='M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z'/><path d='M4 22v-7'/>",
            [Trash] = "<path d='M3 6h18'/><path d='M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6'/><path d='M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2'/>",
            [X] = "<path d='M18 6 6 18'/><path d='m6 6 12 12'/>",
            [ChevronDown] = "<path d='m6 9 6 6 6-6'/>",
            [ArrowLeft] = "<path d='m12 19-7-7 7-7'/><path d='M19 12H5'/>",
            [Refresh] = "<path d='M3 12a9 9 0 1 0 9-9 9.8 9.8 0 0 0-6.7 2.7L3 8'/><path d='M3 3v5h5'/>",
            [AlertTriangle] = "<path d='m21.7 18-8-14a2 2 0 0 0-3.5 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.7-3z'/><path d='M12 9v4'/><path d='M12 17h.01'/>",
            [AlertCircle] = "<circle cx='12' cy='12' r='9.5'/><path d='M12 8v4'/><path d='M12 16h.01'/>",
            [CheckCircle] = "<circle cx='12' cy='12' r='9.5'/><path d='m8.5 12 2.5 2.5 4.5-5'/>",
            [Info] = "<circle cx='12' cy='12' r='9.5'/><path d='M12 16v-4'/><path d='M12 8h.01'/>",
            [Lock] = "<rect width='16' height='10' x='4' y='11' rx='2'/><path d='M8 11V7a4 4 0 0 1 8 0v4'/>",
            [Clock] = "<circle cx='12' cy='12' r='9.5'/><path d='M12 6.5V12l3.5 2'/>",
            [Eye] = "<path d='M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z'/><circle cx='12' cy='12' r='3'/>",
            [EyeOff] = "<path d='M9.9 9.9a3 3 0 1 0 4.2 4.2'/><path d='M10.7 5.1A10 10 0 0 1 12 5c6.5 0 10 7 10 7a13 13 0 0 1-1.7 2.7'/><path d='M6.6 6.6A13.5 13.5 0 0 0 2 12s3.5 7 10 7a9.7 9.7 0 0 0 5.4-1.6'/><path d='m2 2 20 20'/>",
            [Shield] = "<path d='M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z'/><path d='m9 12 2 2 4-4'/>",
            [Ban] = "<circle cx='12' cy='12' r='9.5'/><path d='m5.3 5.3 13.4 13.4'/>",
            [UserCheck] = "<path d='M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='m16 11 2 2 4-4'/>",
            [Pause] = "<circle cx='12' cy='12' r='9.5'/><path d='M10 15V9'/><path d='M14 15V9'/>",
            [Inbox] = "<path d='M22 12h-6l-2 3h-4l-2-3H2'/><path d='M5.5 5.1 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.5-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.7 1.1z'/>",
            [Trending] = "<path d='m22 7-8.5 8.5-5-5L2 17'/><path d='M16 7h6v6'/>",
            [FileText] = "<path d='M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5z'/><path d='M14 2v6h6'/><path d='M16 13H8'/><path d='M16 17H8'/>",
            [Calendar] = "<rect width='18' height='17' x='3' y='4.5' rx='2'/><path d='M16 2.5v4'/><path d='M8 2.5v4'/><path d='M3 10h18'/>",
            [MessageSquare] = "<path d='M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z'/>",
            [Cpu] = "<rect x='4' y='4' width='16' height='16' rx='2'/><rect x='9' y='9' width='6' height='6' rx='1'/><path d='M15 2v2'/><path d='M15 20v2'/><path d='M2 15h2'/><path d='M2 9h2'/><path d='M20 15h2'/><path d='M20 9h2'/><path d='M9 2v2'/><path d='M9 20v2'/>",
            [Gamepad] = "<path d='M6 12h4'/><path d='M8 10v4'/><path d='M15 13h.01'/><path d='M18 11h.01'/><rect width='20' height='12' x='2' y='6' rx='3'/>",
            [Map] = "<path d='M3 6.5 9 3.5l6 3 6-3v14l-6 3-6-3-6 3z'/><path d='M9 3.5v14'/><path d='M15 6.5v14'/>",
            [Lightbulb] = "<path d='M15 14c.2-1 .7-1.7 1.5-2.5A6 6 0 1 0 6 8c0 1 .2 2.2 1.5 3.5.7.7 1.3 1.5 1.5 2.5'/><path d='M9 18h6'/><path d='M10 22h4'/>",
            [Wrench] = "<path d='M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.8-3.8a6 6 0 0 1-7.9 7.9l-6.9 6.9a2.1 2.1 0 0 1-3-3l6.9-6.9a6 6 0 0 1 7.9-7.9z'/>",
            [Megaphone] = "<path d='m3 11 18-5v12L3 14z'/><path d='M11.6 16.8a3 3 0 1 1-5.8-1.6'/>",
            [Pin] = "<path d='M12 17v5'/><path d='M9 10.8a2 2 0 0 1-1.1 1.8l-1.8.9A2 2 0 0 0 5 15.2V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.8a2 2 0 0 0-1.1-1.7l-1.8-.9a2 2 0 0 1-1.1-1.8V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z'/>",
            [SaloonSign] = "<path d='M3 4h18'/><path d='M8 4v4'/><path d='M16 4v4'/><rect x='4' y='8' width='16' height='9' rx='1.5'/><path d='M8 12.5h8'/>",
            [Telegraph] = "<path d='M12 3v18'/><path d='M5 7h14'/><path d='M7 11h10'/><path d='M5 7v1.5'/><path d='M19 7v1.5'/><path d='M7 11v1.5'/><path d='M17 11v1.5'/><path d='M9 21h6'/>",
            [Cards] = "<rect x='3.5' y='6.5' width='10' height='14' rx='1.5' transform='rotate(-10 8.5 13.5)'/><rect x='10' y='3.5' width='10' height='14' rx='1.5'/><path d='m15 8 1.6 2.5L15 13l-1.6-2.5z'/>",
            [Signpost] = "<path d='M12 22V3'/><path d='M12 5h6.5L21 7.5 18.5 10H12'/><path d='M12 12H5.5L3 14.5 5.5 17H12'/><path d='M9 22h6'/>",
            [Lantern] = "<path d='M10 2h4'/><path d='M12 2v2'/><path d='M7 6h10'/><path d='M8 6v12h8V6'/><path d='M7 18h10'/><path d='M12 10v4'/><path d='M10 21h4'/>",
            [Anvil] = "<path d='M3 8h13a4 4 0 0 1-4 4h-1.5l1 3h-5l1-3H6a3 3 0 0 1-3-3z'/><path d='M5 20h11'/><path d='m7.5 15-1 5'/><path d='m13.5 15 1 5'/><path d='m17 7 4-3'/>",
            [NoticeBoard] = "<rect x='3' y='3.5' width='18' height='13' rx='1.5'/><path d='m7 21 2-4.5'/><path d='m17 21-2-4.5'/><rect x='6.5' y='7' width='5' height='6' rx='0.5'/><circle cx='9' cy='7' r='0.9'/><path d='M14 8h3.5'/><path d='M14 11h3.5'/>",
            [Cactus] = "<path d='M10 21V6a2 2 0 0 1 4 0v15'/><path d='M14 12h2a2 2 0 0 0 2-2V8'/><path d='M10 14H8a2 2 0 0 1-2-2v-2'/><path d='M6 21h12'/>",
            [Bell] = "<path d='M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9'/><path d='M10.3 21a1.9 1.9 0 0 0 3.4 0'/>",
            [Menu] = "<path d='M4 6h16'/><path d='M4 12h16'/><path d='M4 18h16'/>",
            [Revolver] = "<path d='M2.5 7.5h15l1.5 1.5v2.5h-9l-1.5 8H4.5l1.3-7H2.5z'/><path d='M19 7.5V6'/><path d='M10.5 11.5v2.5a1.5 1.5 0 0 1-1.5 1.5H8'/><path d='M21.5 9.5h-2.5'/>",
            [Swerve] = "<path d='M4 20c4.5 0 6-3 6-7s2-7 6.5-7H20'/><path d='m17 3 3 3-3 3'/><path d='M4 14h2'/><path d='M4 10h3'/>",
            [Cylinder] = "<circle cx='12' cy='12' r='9'/><circle cx='12' cy='12' r='1.4'/><circle cx='12' cy='6.8' r='1.6'/><circle cx='16.5' cy='9.4' r='1.6'/><circle cx='16.5' cy='14.6' r='1.6'/><circle cx='12' cy='17.2' r='1.6'/><circle cx='7.5' cy='14.6' r='1.6'/><circle cx='7.5' cy='9.4' r='1.6'/>"
        };

        public static string Path(string name) => Paths.TryGetValue(name, out var path) ? path : string.Empty;
    }
}
