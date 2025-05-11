using Microsoft.AspNetCore.Mvc;
using SansPost.Models;
using SansPost.Services;
using System.Threading.Tasks;

[ApiController]
[Route("users")]
public class UsersController : ControllerBase
{
    private readonly UserService _userService;

    public UsersController(UserService userService)
    {
        _userService = userService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] User user)
    {
        var result = await _userService.RegisterUser(user);
        if (!result)
            return BadRequest("Rejestracja nie powiodła się - email już istnieje.");

        return Ok(new { message = "Rejestracja zakończona sukcesem!" });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var token = await _userService.Authenticate(request.Email, request.Password);
        if (string.IsNullOrEmpty(token))
            return Unauthorized("Nieprawidłowy email lub hasło.");

        return Ok(new { Token = token });
    }

    [HttpGet("{userId}")]
    public async Task<IActionResult> GetUser(int userId)
    {
        var user = await _userService.GetUserById(userId);
        if (user == null)
            return NotFound("Użytkownik nie istnieje.");

        return Ok(user);
    }

    [HttpPut("change-role/{userId}")]
    public async Task<IActionResult> ChangeUserRole(int userId, [FromBody] string newRole)
    {
        if (!Enum.TryParse<UserRole>(newRole, out var role))
            return BadRequest("Nieprawidłowa rola użytkownika.");

        var result = await _userService.ChangeUserRole(userId, role);
        if (!result)
            return BadRequest("Nie udało się zmienić roli użytkownika.");

        return Ok(new { message = "Rola użytkownika zmieniona pomyślnie!" });
    }

 

}
