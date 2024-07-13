using Forging.Api.Dtos;
using Forging.Api.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Forging.Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class BaseController : Controller
    {
        private readonly IConfiguration _configuration;

        public BaseController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private NpgsqlConnection GetConnection()
        {
            var connectionString =
                @$"Host={_configuration["DATABASE_HOST_SUPABASE"]};
                                    Port={_configuration["DATABASE_PORT_SUPABASE"]};
                                    Database={_configuration["DEFAULT_DATABASE_NAME"]};
                                    User Id={_configuration["DATABASE_USERNAME_SUPABASE"]};
                                    Password={_configuration["DATABASE_PASSWORD_SUPABASE"]};";
            return new NpgsqlConnection(connectionString);
        }

        [HttpGet("/users")]
        public async Task<ActionResult<IEnumerable<User>>> GetUsers()
        {
            await using var connection = GetConnection();

            await connection.OpenAsync();

            var users = await connection.QueryAsync<User>("SELECT * FROM users");

            await connection.CloseAsync();
            return Ok(users);
        }

        [HttpGet("/users/{id}")]
        public async Task<ActionResult<User>> GetUser(string id)
        {
            await using var connection = GetConnection();

            await connection.OpenAsync();

            var user = await connection.QueryFirstOrDefaultAsync<User>(
                "SELECT * FROM users WHERE id = @Id",
                new { Id = id }
            );

            await connection.CloseAsync();

            if (user == null)
            {
                return NotFound();
            }
            return user;
        }

        [HttpPost("/users")]
        public async Task<ActionResult<User>> CreateUser(CreateUserDto createUserDto)
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            var usersSql =
                @"INSERT INTO users (id, username, email, first_name, last_name, phone_number, image_url, roles) 
            VALUES (@Id, @Username, @Email, @FirstName, @LastName, @PhoneNumber, @ImageUrl, @Roles)";

            var newUser = new User
            {
                Id = createUserDto.Id,
                Username = createUserDto.Username,
                Email = createUserDto.Email,
                PhoneNumber = createUserDto.PhoneNumber,
                Roles = createUserDto.Roles,
                FirstName = createUserDto.FirstName,
                LastName = createUserDto.LastName,
                ImageUrl = createUserDto.ImageUrl,
                JoinedAt = DateTime.UtcNow
            };

            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    var result = await connection.ExecuteAsync(usersSql, newUser, transaction);
                    if (result > 0)
                    {
                        foreach (var email in newUser.Email)
                        {
                            var emailSql =
                                @"INSERT INTO user_emails (id, user_id, email) VALUES (@EmailId, @UserId, @Email)";
                            var insertEmailResult = await connection.ExecuteAsync(
                                emailSql,
                                new
                                {
                                    EmailId = Guid.NewGuid(),
                                    UserId = newUser.Id,
                                    Email = email
                                },
                                transaction
                            );

                            if (insertEmailResult <= 0)
                            {
                                transaction.Rollback();
                                return BadRequest("Failed to insert provided email address(es)");
                            }
                        }

                        foreach (var phoneNumber in newUser.PhoneNumber)
                        {
                            var phoneNrSql =
                                @"INSERT INTO user_phone_numbers (id, user_id, phone_number) VALUES (@PhoneId, @UserId, @PhoneNumber)";
                            var phoneNumberResult = await connection.ExecuteAsync(
                                phoneNrSql,
                                new
                                {
                                    PhoneId = Guid.NewGuid(),
                                    UserId = newUser.Id,
                                    PhoneNumber = phoneNumber
                                },
                                transaction
                            );

                            if (phoneNumberResult <= 0)
                            {
                                transaction.Rollback();
                                return BadRequest("Failed to insert provided phone number(s)");
                            }
                        }

                        foreach (var role in newUser.Roles)
                        {
                            var rolesSql =
                                @"INSERT INTO user_roles (user_id, role_id, role) 
                            VALUES (@UserId, (SELECT id FROM roles WHERE name = @Name), @Name)";
                            var insertRolesResult = await connection.ExecuteAsync(
                                rolesSql,
                                new { UserId = newUser.Id, Name = role },
                                transaction
                            );

                            if (insertRolesResult <= 0)
                            {
                                transaction.Rollback();
                                return BadRequest("Failed to insert provided role(s)");
                            }
                        }

                        transaction.Commit();
                        await connection.CloseAsync();
                        return CreatedAtAction(nameof(GetUser), new { id = newUser.Id }, newUser);
                    }
                    else
                    {
                        transaction.Rollback();
                        return BadRequest("Error at inserting user");
                    }
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return StatusCode(500, "Internal Server Error: " + ex.Message);
                }
            }
        }

        [HttpPut("/users/{id}")]
        public async Task<ActionResult<User>> UpdateUser(string id, UpdateUserDto updateUserDto)
        {
            await using var connection = GetConnection();
            await connection.OpenAsync();

            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    var usersSql =
                        @"UPDATE users 
                    SET username = @Username, email = @Email, first_name = @FirstName, 
                    last_name = @LastName, phone_number = @PhoneNumber, image_url = @ImageUrl, roles = @Roles
                    WHERE id = @Id";

                    var result = await connection.ExecuteAsync(
                        usersSql,
                        new
                        {
                            Id = id,
                            updateUserDto.Username,
                            updateUserDto.Email,
                            updateUserDto.FirstName,
                            updateUserDto.LastName,
                            updateUserDto.PhoneNumber,
                            updateUserDto.ImageUrl,
                            updateUserDto.Roles
                        },
                        transaction
                    );

                    if (result > 0)
                    {
                        var deleteEmailSql = @"DELETE FROM user_emails WHERE user_id = @UserId";
                        var deletePhoneNrSql =
                            @"DELETE FROM user_phone_numbers WHERE user_id = @UserId";
                        var deleteRolesSql = @"DELETE FROM user_roles WHERE user_id = @UserId";

                        await connection.ExecuteAsync(
                            deleteEmailSql,
                            new { UserId = id },
                            transaction
                        );
                        await connection.ExecuteAsync(
                            deletePhoneNrSql,
                            new { UserId = id },
                            transaction
                        );
                        await connection.ExecuteAsync(
                            deleteRolesSql,
                            new { UserId = id },
                            transaction
                        );

                        foreach (var email in updateUserDto.Email)
                        {
                            var insertEmailSql =
                                @"INSERT INTO user_emails (id, user_id, email) VALUES (@EmailId, @UserId, @Email)";
                            var emailResponse = await connection.ExecuteAsync(
                                insertEmailSql,
                                new
                                {
                                    EmailId = Guid.NewGuid(),
                                    UserId = id,
                                    Email = email
                                },
                                transaction
                            );

                            if (emailResponse <= 0)
                            {
                                transaction.Rollback();
                                return BadRequest("Failed to update email address(es)");
                            }
                        }

                        foreach (var phoneNumber in updateUserDto.PhoneNumber)
                        {
                            var insertPhoneNrSql =
                                @"INSERT INTO user_phone_numbers (id, user_id, phone_number) VALUES (@PhoneId, @UserId, @PhoneNumber)";
                            var phoneNumberResponse = await connection.ExecuteAsync(
                                insertPhoneNrSql,
                                new
                                {
                                    PhoneId = Guid.NewGuid(),
                                    UserId = id,
                                    PhoneNumber = phoneNumber
                                },
                                transaction
                            );

                            if (phoneNumberResponse <= 0)
                            {
                                transaction.Rollback();
                                return BadRequest("Failed to update phone number(s)");
                            }
                        }

                        foreach (var role in updateUserDto.Roles)
                        {
                            var rolesSql =
                                @"INSERT INTO user_roles (user_id, role_id, role) 
                            VALUES (@UserId, (SELECT id FROM roles WHERE name = @Name), @Name)";
                            var insertRolesResult = await connection.ExecuteAsync(
                                rolesSql,
                                new { UserId = id, Name = role },
                                transaction
                            );

                            if (insertRolesResult <= 0)
                            {
                                transaction.Rollback();
                                return BadRequest("Failed to update role(s)");
                            }
                        }

                        transaction.Commit();
                        await connection.CloseAsync();
                        return NoContent();
                    }
                    else
                    {
                        transaction.Rollback();
                        return NotFound();
                    }
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return StatusCode(500, "Internal server error: " + ex.Message);
                }
            }
        }

        [HttpDelete("/users/{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            await using var connection = GetConnection();

            await connection.OpenAsync();

            var result = await connection.ExecuteAsync(
                "DELETE FROM users WHERE id = @Id",
                new { Id = id }
            );

            await connection.CloseAsync();
            if (result > 0)
            {
                return NoContent();
            }
            return NotFound();
        }
    }
}
