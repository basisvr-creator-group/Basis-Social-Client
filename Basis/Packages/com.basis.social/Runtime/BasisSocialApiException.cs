using System;

namespace Basis.Social
{
    public sealed class BasisSocialApiException : Exception
    {
        public BasisSocialApiException(long statusCode, string errorCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }

        public long StatusCode { get; }
        public string ErrorCode { get; }
    }
}
