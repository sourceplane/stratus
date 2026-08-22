using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stratus.BuildingBlocks.Web;

/// <summary>
/// The single place a domain <see cref="Result{T}"/> becomes an HTTP response.
/// Controllers stay thin — they translate, they do not decide — and every
/// service answers a failure the same way, in RFC 9457 Problem Details.
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected ActionResult<TResponse> FromResult<TValue, TResponse>(
        Result<TValue> result,
        Func<TValue, TResponse> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return result.IsSuccess
            ? Ok(map(result.Value))
            : ProblemFrom(result.Error);
    }

    protected ActionResult<TResponse> CreatedFromResult<TValue, TResponse>(
        Result<TValue> result,
        Func<TValue, string> location,
        Func<TValue, TResponse> map)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(map);

        return result.IsSuccess
            ? Created(location(result.Value), map(result.Value))
            : ProblemFrom(result.Error);
    }

    private ObjectResult ProblemFrom(Error error) => Problem(
        detail: error.Message,
        statusCode: error.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError,
        },
        title: error.Code);
}
