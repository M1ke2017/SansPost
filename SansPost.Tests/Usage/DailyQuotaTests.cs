using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SansPost.Features;
using SansPost.Features.Comments;
using SansPost.Features.Duels;
using SansPost.Features.Posts;
using SansPost.Features.Usage;
using SansPost.Tests.Duels;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Usage
{
    // Dzienne limity biznesowe (v1.0) na poziomie serwisów: granice 10/11 (posty), 20/21 (komentarze), 15/16 (gry),
    // usunięcie nie zwraca limitu, reset o północy UTC, co liczy się jako gra (trening, start pojedynku, rewanż) a co nie
    // (wyzwanie wysłane / odrzucone / wycofane / wygasłe, faza gotowości), pojedynek: limit obu graczy albo żadnego.
    // Wyścigi równoległych requestów — DailyQuotaPostgresTests i PostConcurrencyTests (prawdziwy PostgreSQL).
    [Trait("Category", "Quota")]
    public sealed class DailyQuotaTests : IDisposable
    {
        private readonly TestDatabase _db = new();

        public void Dispose() => _db.Dispose();

        private static PostRequest Post(string title) => new() { Title = title, Content = "Treść", Category = PostCategory.General };

        private PostService Posts(SansPost.Infrastructure.Persistence.ApplicationDbContext context) => new(context, _db.Time, TestServices.Guard(context));

        [Fact]
        public async Task Posts_TenPerUtcDay_EleventhRejected_DeleteDoesNotRestore_NextDayResets()
        {
            var user = await _db.RegisterAsync(TestUsers.UniqueName());
            using var context = _db.CreateContext();
            var service = Posts(context);

            PostDetailsResponse? first = null;
            for (var i = 0; i < DailyQuota.Posts; i++)
            {
                var created = await service.CreateAsync(user.Id, Post($"Post {i}"));
                Assert.True(created.Succeeded, created.Message);
                first ??= created.Value;
            }
            var eleventh = await service.CreateAsync(user.Id, Post("Jedenasty"));
            Assert.Equal(ServiceError.RateLimited, eleventh.Error);
            Assert.Equal(DailyQuota.PostLimitCode, eleventh.Code);
            Assert.InRange(eleventh.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1));

            // Usunięcie posta nie zwalnia dziennego limitu.
            Assert.True((await service.DeleteAsync(user.Id, first!.Id, first.Version)).Succeeded);
            Assert.Equal(DailyQuota.PostLimitCode, (await service.CreateAsync(user.Id, Post("Po usunięciu"))).Code);
            Assert.Equal(new PostQuotaResponse(10, 10, 0), await service.GetQuotaAsync(user.Id));

            // Nowa doba UTC — limit od zera.
            _db.Time.Advance(DailyQuota.UntilReset(_db.Time.GetUtcNow()) + TimeSpan.FromSeconds(1));
            Assert.True((await service.CreateAsync(user.Id, Post("Nowy dzień"))).Succeeded);
            Assert.Equal(new PostQuotaResponse(10, 1, 9), await service.GetQuotaAsync(user.Id));
        }

        [Fact]
        public async Task Comments_TwentyPerUtcDay_TwentyFirstRejected()
        {
            var author = await _db.RegisterAsync(TestUsers.UniqueName());
            var commenter = await _db.RegisterAsync(TestUsers.UniqueName());
            using var context = _db.CreateContext();
            var post = (await Posts(context).CreateAsync(author.Id, Post("Rozmowa"))).Value!;
            var comments = TestServices.Comments(context, _db.Time);

            for (var i = 0; i < DailyQuota.Comments; i++)
                Assert.True((await comments.AddAsync(commenter.Id, post.Id, new CommentRequest { Content = $"Komentarz {i}" })).Succeeded);
            var over = await comments.AddAsync(commenter.Id, post.Id, new CommentRequest { Content = "Dwudziesty pierwszy" });

            Assert.Equal(ServiceError.RateLimited, over.Error);
            Assert.Equal(DailyQuota.CommentLimitCode, over.Code);
            Assert.Equal(DailyQuota.Comments, await context.UsedTodayAsync(commenter.Id, QuotaKind.Comment, _db.Time.GetUtcNow()));
            // Limit komentarzy nie dotyka limitu postów autora komentarza.
            Assert.Equal(0, await context.UsedTodayAsync(commenter.Id, QuotaKind.Post, _db.Time.GetUtcNow()));
        }
    }

    // Gry: serwisy pojedynków na SQLite (DuelAccounts), czas ręczny.
    [Trait("Category", "Quota")]
    [Trait("Category", "Duels")]
    public sealed class DailyGameQuotaTests : IDisposable
    {
        private readonly DuelAccounts _accounts = new();
        private readonly ManualTimeProvider _time = new();
        private readonly RecordingNotifier _events = new();
        private readonly DuelOptions _options = new();
        private readonly GameSessionService _sessions;
        private readonly DuelConnectionRegistry _registry = new();
        private readonly DuelChallengeService _challenges;
        private readonly int Anna, Bart, Cole;

        public DailyGameQuotaTests()
        {
            Anna = _accounts.Add();
            Bart = _accounts.Add();
            Cole = _accounts.Add();
            _sessions = _accounts.Sessions(_time, _events, _options);
            _challenges = new DuelChallengeService(_time, _sessions, _registry, _accounts.Scopes, _events, Options.Create(_options),
                NullLogger<DuelChallengeService>.Instance);
        }

        public void Dispose()
        {
            _challenges.Dispose();
            _sessions.Dispose();
            _accounts.Dispose();
        }

        private static T Ok<T>(ServiceResult<T> result)
        {
            Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
            return result.Value!;
        }

        private int Games(int userId)
        {
            using var context = _accounts.CreateContext();
            var today = DailyQuota.Today(_time.GetUtcNow());
            return context.DailyUserUsages.AsNoTracking().Where(u => u.UserId == userId && u.DateUtc == today).Select(u => u.GamesStarted).SingleOrDefault();
        }

        private void SeedGames(int userId, int games)
        {
            using var context = _accounts.CreateContext();
            context.DailyUserUsages.Add(new DailyUserUsage { UserId = userId, DateUtc = DailyQuota.Today(_time.GetUtcNow()), GamesStarted = games });
            context.SaveChanges();
        }

        private string Online(int userId, string alias)
        {
            _registry.Add(userId, alias, Guid.NewGuid().ToString("N"));
            return _registry.Online().Single(p => p.UserId == userId).Handle;
        }

        private async Task<Guid> ReadyBothAsync(DuelSnapshot duel)
        {
            var id = Guid.Parse(duel.DuelId);
            Ok(await _sessions.SetReadyAsync(id, Anna));
            Ok(await _sessions.SetReadyAsync(id, Bart));
            return id;
        }

        [Fact]
        public async Task Training_FifteenPerUtcDay_SixteenthRejected()
        {
            for (var i = 0; i < DailyQuota.Games; i++)
                Ok(await _sessions.CreateAsync(Anna, "Anna", DuelMode.Training));
            var over = await _sessions.CreateAsync(Anna, "Anna", DuelMode.Training);

            Assert.Equal(ServiceError.RateLimited, over.Error);
            Assert.Equal(DailyQuota.GameLimitCode, over.Code);
            Assert.Equal(DailyQuota.Games, Games(Anna));

            _time.Advance(DailyQuota.UntilReset(_time.GetUtcNow()) + TimeSpan.FromSeconds(1));
            Ok(await _sessions.CreateAsync(Anna, "Anna", DuelMode.Training));
            Assert.Equal(1, Games(Anna));
        }

        // Wyzwanie REST: utworzenie nie jest grą, dołączenie drugiego gracza — tak (u obu).
        [Fact]
        public async Task RestChallenge_CreateNotCounted_JoinCountsBothPlayers()
        {
            var duel = Ok(await _sessions.CreateAsync(Anna, "Anna", DuelMode.Challenge));
            Assert.Equal(0, Games(Anna));

            Ok(await _sessions.JoinAsync(Guid.Parse(duel.DuelId), Bart, "Bart"));
            Ok(await _sessions.JoinAsync(Guid.Parse(duel.DuelId), Bart, "Bart"));   // powtórzone dołączenie — bez drugiej gry

            Assert.Equal(1, Games(Anna));
            Assert.Equal(1, Games(Bart));
        }

        // Wyzwanie wysłane / odrzucone / wycofane / wygasłe i faza gotowości — bez zużycia; start, oddanie po starcie
        // i rewanż — liczone.
        [Fact]
        public async Task LiveDuel_OnlyActualStartsCount_RematchCountsAgain()
        {
            Online(Anna, "Anna");
            var bart = Online(Bart, "Bart");
            var cole = Online(Cole, "Cole");

            var rejected = Ok(await _challenges.ChallengeAsync(Anna, "Anna", bart));
            Ok(await _challenges.RejectAsync(Bart, rejected.ChallengeId));
            Ok(await _challenges.ChallengeAsync(Anna, "Anna", cole));
            Ok(await _challenges.CancelAsync(Anna));
            Ok(await _challenges.ChallengeAsync(Bart, "Bart", cole));
            _time.Advance(_options.ChallengeLifetime + TimeSpan.FromSeconds(1));   // wygasło
            Assert.Equal((0, 0, 0), (Games(Anna), Games(Bart), Games(Cole)));

            // Przyjęte wyzwanie = faza gotowości (jeszcze nie gra), potem start po "GOTOWY" obu.
            var duel = Ok(await _sessions.StartLiveAsync(Anna, "Anna", Bart, "Bart"));
            Assert.Equal((0, 0), (Games(Anna), Games(Bart)));
            var id = await ReadyBothAsync(duel);
            Assert.Equal("in-progress", Ok(_sessions.Get(id, Anna)).Status);
            Assert.Equal((1, 1), (Games(Anna), Games(Bart)));

            // Oddanie po starcie — gra zostaje policzona; rewanż to kolejna gra.
            Ok(await _sessions.SurrenderAsync(id, Anna));
            Assert.Equal((1, 1), (Games(Anna), Games(Bart)));
            Ok(await _sessions.RequestRematchAsync(id, Anna));
            var rematch = Ok(await _sessions.RequestRematchAsync(id, Bart));
            await ReadyBothAsync(rematch);
            Assert.Equal((2, 2), (Games(Anna), Games(Bart)));
        }

        // A: 14/15, B: 15/15, obaj "GOTOWY" → gra nie startuje, nikt nic nie traci; B przestaje być "gotowy".
        [Fact]
        public async Task LiveDuel_OpponentAtLimit_DoesNotStart_BothOrNeither()
        {
            SeedGames(Anna, DailyQuota.Games - 1);
            SeedGames(Bart, DailyQuota.Games);
            var duel = Ok(await _sessions.StartLiveAsync(Anna, "Anna", Bart, "Bart"));
            var id = Guid.Parse(duel.DuelId);

            Ok(await _sessions.SetReadyAsync(id, Bart));
            var last = await _sessions.SetReadyAsync(id, Anna);

            Assert.Equal(DailyQuota.OpponentGameLimitCode, last.Code);   // A dostaje ogólny komunikat — bez licznika B
            Assert.DoesNotContain("15", last.Message);
            var view = Ok(_sessions.Get(id, Anna));
            Assert.Equal("ready-check", view.Status);
            Assert.True(view.You.StartReady);
            Assert.False(view.Opponent!.StartReady);
            Assert.Equal((DailyQuota.Games - 1, DailyQuota.Games), (Games(Anna), Games(Bart)));

            // B próbuje ponownie — własny komunikat o limicie.
            Assert.Equal(DailyQuota.GameLimitCode, (await _sessions.SetReadyAsync(id, Bart)).Code);
        }

        [Fact]
        public async Task Challenge_PlayerWithoutGamesLeft_CannotChallengeOrAccept()
        {
            Online(Anna, "Anna");
            var bart = Online(Bart, "Bart");
            SeedGames(Anna, DailyQuota.Games);

            Assert.Equal(DailyQuota.GameLimitCode, (await _challenges.ChallengeAsync(Anna, "Anna", bart)).Code);

            var annaHandle = _registry.Online().Single(p => p.UserId == Anna).Handle;
            var challenge = Ok(await _challenges.ChallengeAsync(Bart, "Bart", annaHandle));
            Assert.Equal(DailyQuota.GameLimitCode, (await _challenges.AcceptAsync(Anna, challenge.ChallengeId)).Code);
            Assert.Equal(0, Games(Bart));
        }
    }
}
