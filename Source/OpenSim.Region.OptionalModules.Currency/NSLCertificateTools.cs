/* 
 * Copyright (c) Contributors, http://www.nsl.tuis.ac.jp
 *
 */

using Microsoft.Extensions.Logging;

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;


namespace NSL.Certificate.Tools
{
    /// <summary>
    /// class NSL Certificate Verify
    /// </summary>
    public class NSLCertificateVerify
    {
        private X509Chain? m_chain = null;
        private X509Certificate2? m_cacert = null;
        private Mono.Security.X509.X509Crl? m_clientcrl = null;

        private readonly ILogger<NSLCertificateVerify> _logger;


        /// <summary>
        /// NSL Certificate Verify
        /// </summary>
        public NSLCertificateVerify(ILogger<NSLCertificateVerify> logger)
        {
            _logger = logger;

            m_chain = null;
            m_cacert = null;
            m_clientcrl = null;
        }


        /// <summary>
        /// NSL Certificate Verify
        /// </summary>
        /// <param name="certfile"></param>
        public NSLCertificateVerify(ILogger<NSLCertificateVerify> logger, string certfile)
        {
            _logger = logger;

            SetPrivateCA(certfile);
        }


        /// <summary>
        /// NSL Certificate Verify
        /// </summary>
        /// <param name="certfile"></param>
        /// <param name="crlfile"></param>
        public NSLCertificateVerify(ILogger<NSLCertificateVerify> logger, string certfile, string crlfile)
        {
            _logger = logger;

            SetPrivateCA(certfile);
            SetPrivateCRL(crlfile);
        }


        /// <summary>
        /// Set Private CA
        /// </summary>
        /// <param name="certfile"></param>
        public void SetPrivateCA(string certfile)
        {
            try
            {
                m_cacert = new X509Certificate2(certfile);
            }
            catch (Exception ex)
            {
                m_cacert = null;
                _logger.LogError(ex, $"[SET PRIVATE CA]: CA File reading error [{certfile}].");
            }

            if (m_cacert != null)
            {
                m_chain = new X509Chain();
                m_chain.ChainPolicy.ExtraStore.Add(m_cacert);
                m_chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                m_chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            }
        }


        //
        public void SetPrivateCRL(string crlfile)
        {
            try
            {
                m_clientcrl = Mono.Security.X509.X509Crl.CreateFromFile(crlfile);
            }
            catch (Exception ex)
            {
                m_clientcrl = null;
                _logger.LogError($"[SET PRIVATE CRL]: CRL File reading error [{crlfile}].");
            }
        }


        /// <summary>
        /// Check Private Chain
        /// </summary>
        /// <param name="cert"></param>
        /// <returns></returns>
        public bool CheckPrivateChain(X509Certificate2 cert)
        {
            if (m_chain == null || m_cacert == null)
            {
                return false;
            }

            bool ret = m_chain.Build((X509Certificate2)cert);
            if (ret)
            {
                return true;
            }

            for (int i = 0; i < m_chain.ChainStatus.Length; i++)
            {
                if (m_chain.ChainStatus[i].Status == X509ChainStatusFlags.UntrustedRoot) return true;
            }

            return false;
        }


        /*
        SslPolicyErrors:
            RemoteCertificateNotAvailable = 1, // 証明書が利用できません．
            RemoteCertificateNameMismatch = 2, // 証明書名が不一致です．
            RemoteCertificateChainErrors  = 4, // ChainStatus が空でない配列を返しました．
        */


        /// <summary>
        /// Validate Server Certificate
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="certificate"></param>
        /// <param name="chain"></param>
        /// <param name="sslPolicyErrors"></param>
        /// <returns></returns>
        public bool ValidateServerCertificate(object obj, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            _logger.LogInformation("[NSL SERVER CERT VERIFY]: ValidateServerCertificate: Start.");

            if (obj is HttpWebRequest)
            {
                HttpWebRequest Request = (HttpWebRequest)obj;
                string? noVerify = Request.Headers.Get("NoVerifyCert");

                if (noVerify is not null && noVerify.ToLower() == "true")
                {
                    _logger.LogInformation($"[NSL SERVER CERT VERIFY]: ValidateServerCertificate: No Verify Certificate.");
                    return true;
                }
            }

            X509Certificate2 certificate2 = new X509Certificate2(certificate);
            string simplename = certificate2.GetNameInfo(X509NameType.SimpleName, false);

            // None, ChainErrors Error except for．
            if (sslPolicyErrors != SslPolicyErrors.None && sslPolicyErrors != SslPolicyErrors.RemoteCertificateChainErrors)
            {
                _logger.LogError($"[NSL SERVER CERT VERIFY]: ValidateServerCertificate: Policy Error! {sslPolicyErrors}");
                return false;
            }

            bool valid = CheckPrivateChain(certificate2);

            if (valid)
            {
                _logger.LogInformation($"[NSL SERVER CERT VERIFY]: ValidateServerCertificate: Valid Server Certification for \"{simplename}\"");
            }
            else
            {
                _logger.LogInformation($"[NSL SERVER CERT VERIFY]: ValidateServerCertificate: Failed to Verify Server Certification for \"{simplename}\"");
            }

            return valid;
        }


        /// <summary>
        /// Validate Client Certificate
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="certificate"></param>
        /// <param name="chain"></param>
        /// <param name="sslPolicyErrors"></param>
        /// <returns></returns>
        public bool ValidateClientCertificate(object obj, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            _logger.LogInformation($"[NSL CLIENT CERT VERIFY]: ValidateClientCertificate: Start");

            X509Certificate2 certificate2 = new X509Certificate2(certificate);
            string simplename = certificate2.GetNameInfo(X509NameType.SimpleName, false);
            _logger.LogInformation($"[NSL CLIENT CERT VERIFY]: ValidateClientCertificate: Simple Name is \"{simplename}\"");

            // None, ChainErrors
            if (sslPolicyErrors != SslPolicyErrors.None && sslPolicyErrors != SslPolicyErrors.RemoteCertificateChainErrors)
            {
                _logger.LogInformation($"[NSL CLIENT CERT VERIFY]: ValidateClientCertificate: Policy Error! {sslPolicyErrors}");
                return false;
            }

            // check CRL
            if (m_clientcrl != null)
            {
                Mono.Security.X509.X509Certificate monocert = new Mono.Security.X509.X509Certificate(certificate.GetRawCertData());
                Mono.Security.X509.X509Crl.X509CrlEntry entry = m_clientcrl.GetCrlEntry(monocert);

                if (entry != null)
                {
                    _logger.LogInformation($"[NSL CLIENT CERT VERIFY]: Common Name \"{simplename}\" was revoked at {entry.RevocationDate}");
                    return false;
                }
            }

            bool valid = CheckPrivateChain(certificate2);

            if (valid)
            {
                _logger.LogInformation($"[NSL CLIENT CERT VERIFY]: Valid Client Certification for \"{simplename}\"");
            }
            else
            {
                _logger.LogInformation($"[NSL CLIENT CERT VERIFY]: Failed to Verify Client Certification for \"{simplename}\"");
            }

            return valid;
        }
    }
}
