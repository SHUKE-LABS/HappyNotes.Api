using Api.Framework;
using Api.Framework.Extensions;
using Api.Framework.Helper;
using Api.Framework.Models;
using Api.Framework.Result;
using AutoMapper;
using HappyNotes.Dto;
using HappyNotes.Entities;
using HappyNotes.Models;
using HappyNotes.Services.interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HappyNotes.Api.Controllers
{
    [Authorize]
    public class AccountController(
        IOptions<JwtConfig> jwtConfig,
        IRepositoryBase<User> userRepository,
        IAccountService accountService,
        IGoogleAuthService googleAuthService,
        IMapper mapper,
        ICurrentUser currentUser)
        : BaseController
    {
        private readonly JwtConfig _jwtConfig = jwtConfig.Value;
        private const int TokenExpiresInDays = 180;

        /// <summary>
        /// Refresh token
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        public SuccessfulResult<JwtToken> RefreshToken()
        {
            var claims = TokenHelper.ClaimsGenerator(currentUser.Id, currentUser.Username, currentUser.Email);

            var token = TokenHelper.JwtTokenGenerator(claims, _jwtConfig.Issuer, _jwtConfig.SymmetricSecurityKey, TokenExpiresInDays);
            return new SuccessfulResult<JwtToken>(new JwtToken { Token = token, });
        }

        [AllowAnonymous]
        [HttpPost]
        public async Task<ApiResult<JwtToken>> Register(RegisterRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                throw new ArgumentException("Username or Password cannot be empty");
            }

            request.Username = request.Username.Trim();
            request.Email = request.Email.Trim().ToLower();

            var user = await userRepository.GetFirstOrDefaultAsync(where =>
                where.Username.Equals(request.Username.Trim()));
            if (user != null)
            {
                throw new Exception($"Username {request.Username.Trim()} was already taken by someone else");
            }

            string gravatar = String.Empty;
            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                user = await userRepository.GetFirstOrDefaultAsync(w => w.Email.Equals(request.Email.Trim().ToLower()));
                if (user != null)
                {
                    throw new Exception($"Email {request.Email.Trim()} was already being used by another account");
                }

                gravatar = GravatarHelper.GetGravatarUrl(request.Email);
            }

            var (salt, password) = CommonHelper.GetSaltedPassword(request.Password);
            var newUser = new User
            {
                Username = request.Username,
                Email = request.Email,
                Gravatar = gravatar,
                Salt = salt,
                Password = password,
                CreatedAt = DateTime.Now.ToUnixTimeSeconds(),
            };

            var id = await userRepository.InsertReturnIdentityAsync(newUser);

            var claims = TokenHelper.ClaimsGenerator(id, request.Username, request.Email);
            var jwtToken = TokenHelper.JwtTokenGenerator(claims, _jwtConfig.Issuer, _jwtConfig.SymmetricSecurityKey, TokenExpiresInDays);

            return new SuccessfulResult<JwtToken>(new JwtToken { Token = jwtToken, });
        }


        [AllowAnonymous]
        [HttpPost]
        public async Task<ApiResult<JwtToken>> Login(LoginRequest loginRequest)
        {
            if (string.IsNullOrWhiteSpace(loginRequest.Username) || string.IsNullOrWhiteSpace(loginRequest.Password))
            {
                throw new ArgumentException("Username or Password cannot be empty");
            }

            var validateResult = await accountService.IsValidLogin(loginRequest.Username, loginRequest.Password);
            if (!validateResult)
            {
                throw new Exception("Username or Password is incorrect");
            }

            var user = await userRepository.GetFirstOrDefaultAsync(
                where => where.Username.Equals(loginRequest.Username));
            if (user == null)
            {
                throw new Exception("Unexpected error, user not found.");
            }

            if (user.DeletedAt != null)
            {
                throw new Exception("Sorry, your account has been deleted");
            }

            var claims = TokenHelper.ClaimsGenerator(user.Id, user.Username, user.Email);
            var jwtToken = TokenHelper.JwtTokenGenerator(claims, _jwtConfig.Issuer, _jwtConfig.SymmetricSecurityKey, TokenExpiresInDays);

            return new SuccessfulResult<JwtToken>(new JwtToken { Token = jwtToken, });
        }

        [AllowAnonymous]
        [HttpPost]
        public async Task<ApiResult<JwtToken>> GoogleLogin(GoogleLoginRequest request)
        {
            var user = await googleAuthService.ResolveOrCreateUserAsync(request.IdToken);

            var claims = TokenHelper.ClaimsGenerator(user.Id, user.Username, user.Email);
            var jwtToken = TokenHelper.JwtTokenGenerator(claims, _jwtConfig.Issuer, _jwtConfig.SymmetricSecurityKey, TokenExpiresInDays);

            return new SuccessfulResult<JwtToken>(new JwtToken { Token = jwtToken, });
        }

        [HttpGet]
        public async Task<ApiResult<UserDto>> MyInformation()
        {
            var user = await userRepository.GetFirstOrDefaultAsync(where => where.Id == currentUser.Id);
            var userDto = mapper.Map<UserDto>(user);
            return new SuccessfulResult<UserDto>(userDto);
        }

        /// <summary>
        /// Change user password
        /// </summary>
        /// <param name="request">Object containing current and new passwords</param>
        /// <returns>Success or failure result</returns>
        [HttpPost]
        public async Task<ApiResult> ChangePassword(ChangePasswordRequest request)
        {
            if (request.CurrentPassword.Equals(request.NewPassword))
            {
                throw new ArgumentException("Current password and new password cannot be the same");
            }
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                throw new ArgumentException("Current password and new password cannot be empty");
            }

            var userId = currentUser.Id;
            var success = await accountService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword);
            return success
                ? Success("Password changed successfully")
                : Fail("Failed to change password. Current password may be incorrect.");
        }
    }
}
