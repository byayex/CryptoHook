using Microsoft.AspNetCore.Mvc;

namespace CryptoHook.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PaymentController : ControllerBase
{

}