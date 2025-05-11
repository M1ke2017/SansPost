using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using System.Text;
using SansPost.Data;
using SansPost.Services;
using SansPost.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using System.Net.NetworkInformation;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using SansPost.Models.Enum;

namespace SansPost;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Konfiguracja JWT
        var jwtKey = builder.Configuration["Jwt:Key"];
        if (string.IsNullOrEmpty(jwtKey))
        {
            throw new Exception("Brak klucza JWT w konfiguracji!");
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var key = Encoding.UTF8.GetBytes(jwtKey);

                options.RequireHttpsMetadata = false;
                options.SaveToken = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = false,
                    ValidateAudience = false
                };
            });

        builder.Services.AddAuthorization();

        // Konfiguracja bazy danych PostgreSQL
        builder.Services.AddDbContext<AppliactionDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

        // Dodanie obs³ugi CORS (umo¿liwia po³¹czenia z innych aplikacji)
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowSpecificOrigins", policy =>
            {
                policy.WithOrigins() 
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials(); 
            });
        });

        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.WriteIndented = true;
        });



        // Rejestracja serwisów
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();
        builder.Services.AddScoped<UserService>();
        builder.Services.AddBlazoredLocalStorage();
        builder.Services.AddScoped<AuthService>();
        builder.Services.AddScoped<PostService>();
        builder.Services.AddScoped<CommentService>();
        builder.Services.AddScoped<LikeService>();
        builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri("https://localhost:7101/") });
        builder.Services.AddScoped<CustomAuthStateProvider>();
        builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthStateProvider>());
        var app = builder.Build();


        // Konfiguracja œcie¿ki b³êdów
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler(app =>
            {
                app.Run(async context =>
                {
                    context.Response.ContentType = "application/json";
                    var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerFeature>();

                    if (exceptionHandlerPathFeature?.Error != null)
                    {
                        var errorResponse = new
                        {
                            success = false,
                            message = "Wyst¹pi³ b³¹d wewnêtrzny",
                            error = exceptionHandlerPathFeature.Error.Message
                        };

                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(errorResponse));
                    }
                });
            });
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        app.UseRouting();

        // Aktywacja CORS
        app.UseCors("AllowAllOrigins");

        // Aktywacja JWT i autoryzacji
        app.UseAuthentication();
        app.UseAuthorization();

        // Mapowanie API
        app.MapControllers();

        app.MapGet("/secure-data", [Authorize] () =>
        {
            Console.WriteLine("TESTOWE ZWRACANIE DANYCH");
            return Results.Json(ApiResponse<string>.Ok("Access for logged in users only"), statusCode: 200);
        }); // Testowanie API

        app.MapGet("/admin-data", [Authorize(Roles = "admin")] () =>
        {
            return Results.Json(ApiResponse<string>.Ok("Administrator-only access"), statusCode: 200);
        });

        app.MapPost("/users/register", async (RegisterUserRequest request, AuthService authService) =>
        {
            if (request == null)
                return Results.Json(ApiResponse<string>.Fail("No data available."), statusCode: 400);

            if (string.IsNullOrWhiteSpace(request.Password))
                return Results.Json(ApiResponse<string>.Fail("Password cannot be empty."), statusCode: 400);

            try
            {
                bool success = await authService.RegisterUser(request.Username, request.Email, request.Password);
                return success ? Results.Json(ApiResponse<string>.Ok("User registered"), statusCode: 200)
                               : Results.Json(ApiResponse<string>.Fail("Registration failed"), statusCode: 400);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Registration error: " + ex.Message);
                return Results.Json(ApiResponse<string>.Fail("Internal server error"), statusCode: 500);
            }

        });

        app.MapPost("users/login", async (LoginRequest request, AuthService authService) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.Json(ApiResponse<string>.Fail("Invalid data"), statusCode: 400);

            string? token = await authService.LoginUser(request.Email, request.Password);

            if (token == null)
                return Results.Json(ApiResponse<string>.Fail("Incorrect login details"), statusCode: 400);

            return Results.Json(ApiResponse<string>.Ok(token, "Login successful"), statusCode: 200);
        });

        app.MapPost("/add-post", [Authorize] async (PostService postService, HttpContext context, PostRequest request) =>
        {
            var userId = int.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Results.Json(ApiResponse<string>.Fail("Unauthorized access"), statusCode: 401);

            bool success = await postService.AddPost(
                userId,
                request.Title,
                request.Content,
                request.Category.ToString(),
                request.ImageUrl ?? string.Empty
            );

            return success
                ? Results.Json(ApiResponse<string>.Ok("Post added"), statusCode: 200)
                : Results.Json(ApiResponse<string>.Fail("Post limit reached"), statusCode: 400);
        });

        app.MapGet("/my-posts", [Authorize] async (PostService postService, HttpContext context) =>
        {
            var userId = int.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Results.Json(ApiResponse<string>.Fail("Unauthorized access"), statusCode: 401);

            var posts = await postService.GetUserPosts(userId);
            return Results.Ok(new ApiResponse<List<Post>>(true, "User potst retrived", posts));
        });

        app.MapDelete("/delete-post/{id}", [Authorize] async (int id, PostService postService, HttpContext context) =>
        {
            var userId = int.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Results.Json(ApiResponse<string>.Fail("Unauthorized access"), statusCode: 401);

            bool success = await postService.DeletePost(userId, id);

            return success
            ? Results.Json(ApiResponse<string>.Ok("Post deleted"), statusCode: 200)
            : Results.Json(ApiResponse<string>.Fail("You can't delete this post."), statusCode: 400);
        });
        app.MapPut("/edit-post/{id:int}", [Authorize] async (
                int id,
                PostRequest updatedPost,
                HttpContext context,
                AppliactionDbContext db) =>
        {
            // Pobieramy userId z tokena
            var userIdRaw = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdRaw, out var userId) || userId == 0)
            {
                Console.WriteLine("[DEBUG] Brak poprawnego userId z tokena");
                return Results.Unauthorized();
            }

            Console.WriteLine($"[DEBUG] userId: {userId}, postId: {id}");

            var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null)
            {
                Console.WriteLine($"[DEBUG] Post ID {id} nie istnieje.");
                return Results.NotFound("Post nie istnieje.");
            }

            Console.WriteLine($"[DEBUG] Post.UserId: {post.UserId}, Authenticated userId: {userId}");

            if (post.UserId != userId)
            {
                Console.WriteLine("[DEBUG] Brak uprawnieñ do edycji tego posta.");
                return Results.Unauthorized();
            }


            // Aktualizujemy post
            post.Title = updatedPost.Title;
            post.Content = updatedPost.Content;
            post.Category = updatedPost.Category.ToString();
            post.ImageUrl = updatedPost.ImageUrl;

            await db.SaveChangesAsync();

            Console.WriteLine("[DEBUG] Post zosta³ zaktualizowany");

            return Results.Ok(new ApiResponse<string>(true, "Post zaktualizowany!", null));
        });


        app.MapPut("/change-role/{userId}", [Authorize] async (int userId, HttpContext context, UserService userService, UserRole newRole) =>
        {
            var adminId = int.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var admin = await userService.GetUserById(adminId);

            if (admin == null || admin.Role != UserRole.Admin)
                return Results.Json(ApiResponse<string>.Fail("Unauthorized access"), statusCode: 401);

            bool success = await userService.ChangeUserRole(userId, newRole);
            return success
            ? Results.Ok(new ApiResponse<string>(true, "Role changed successfully", null))
            : Results.BadRequest(new ApiResponse<string>(false, "Failed to change role", null));

        });

        app.MapPost("/set-premium/{userId}", [Authorize(Roles = "admin")] async (int userId, AppliactionDbContext context) =>
        {
            var user = await context.Users.Include(u => u.Subscription).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return Results.Json(ApiResponse<string>.Fail("User not found"), statusCode: 404);

            if (user.Subscription == null)
            {
                // Tworzymy now¹ subskrypcjê
                user.Subscription = new Subscription
                {
                    UserId = user.Id,
                    Type = SubscriptionType.Premium,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddYears(1) //zmiana na dowolny okres
                };
                context.Subscriptions.Add(user.Subscription);
            }
            else
            {
                // Aktualizujemy istniej¹c¹ subskrypcjê
                user.Subscription.Type = SubscriptionType.Premium;
                user.Subscription.ExpiresAt = DateTime.UtcNow.AddYears(1);
            }

            await context.SaveChangesAsync();
            return Results.Json(ApiResponse<string>.Ok($"User {user.Username} is now Premium!"), statusCode: 200);
        });


        app.MapGet("/remaining-posts", [Authorize] async (PostService postService, HttpContext context) =>
        {
            var userId = int.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Results.Json(ApiResponse<string>.Fail("Unauthorized access"), statusCode: 401);

            int remainingPosts = await postService.GetRemainingPosts(userId);
            return Results.Ok(new ApiResponse<int>(true, "Reamining posts count", remainingPosts));
        });

        app.MapGet("/api/posts", async (PostService postService) =>
        {
            var posts = await postService.GetAllPosts();
            return Results.Json(ApiResponse<List<Post>>.Ok(posts, "Posts downloaded"), statusCode: 200);
        });

        app.MapGet("/posts/{postId}/comments", async (int postId, CommentService commentService) =>
        {
            var comments = await commentService.GetCommentsByPost(postId);
            return Results.Ok(comments);
        });

        app.MapPost("/posts/{postId}/comments", async (int postId, HttpContext context, CommentService commentService, string content) =>
        {
            var userId = int.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Results.Unauthorized();

            await commentService.AddComment(postId, userId, content);
            return Results.Ok();
        });

        app.MapDelete("/comments/{commentId}", async (int commentId, HttpContext context, CommentService commentService) =>
        {
            var userId = int.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Results.Unauthorized();

            var success = await commentService.DeleteComment(commentId, userId);
            return success ? Results.Ok() : Results.BadRequest();
        });

        app.MapGet("/posts/{postId}/likes-count", async (int postId, LikeService likeService) =>
        {
            var count = await likeService.GetLikeCount(postId);
            return Results.Ok(count);
        });

        app.MapPost("/posts/{postId}/like", async (int postId, HttpContext context, LikeService likeService) =>
        {
            var userId = int.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Results.Unauthorized();

            await likeService.ToggleLike(postId, userId);
            return Results.Ok();
        });

        app.MapGet("/post/{id:int}", async (int id, AppliactionDbContext context) =>
        {
            var post = await context.Posts
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.Id == id);

            return post is not null
                ? Results.Ok(post)
                : Results.NotFound();
        });


        app.MapGet("/all-posts", async (AppliactionDbContext context) =>
        {
            var posts = await context.Posts.Include(p => p.User)
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Content,
                p.Category,
                p.ImageUrl,
                p.CreatedAt,
                Author = p.User.Username
            })
            .ToListAsync();

            return Results.Ok(posts);
        });
        // Mapowanie Blazor musi byæ na koñcu, aby nie nadpisywaæ API!*

        app.MapControllers();
        app.MapBlazorHub();
        app.MapFallbackToPage("/_Host");
       

        app.Run();
    }
}
