/* 
 * Copyright (c) Contributors, http://www.nsl.tuis.ac.jp
 *
 */


using System;
using System.Collections;
using System.IO;
using System.Xml;
using System.Net;
using System.Text;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using log4net;
using Nwc.XmlRpc;
using System.Net.Security;
using NSL.Certificate.Tools;


namespace NSL.Network.XmlRpc
{
    public class NSLXmlRpcRequest : XmlRpcRequest
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // The encoding
        private Encoding _encoding = new UTF8Encoding();
        // The serializer
        private XmlRpcRequestSerializer _serializer = new XmlRpcRequestSerializer();
        // The deserializer
        private XmlRpcResponseDeserializer _deserializer = new XmlRpcResponseDeserializer();


        /// <summary>
        /// Initializes a new instance of the <see cref="NSLXmlRpcRequest" /> class.
        /// </summary>
        /// <remarks>
        /// This constructor initializes the request with an empty parameter list.
        /// </remarks>
        public NSLXmlRpcRequest()
        {
            // Initialize the parameter list as an empty ArrayList
            _params = new ArrayList();
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="NSLXmlRpcRequest" /> class.
        /// </summary>
        /// <param name="methodName">Name of the method to be invoked in the XML-RPC request.</param>
        /// <param name="parameters">The parameters to be passed to the method.</param>
        public NSLXmlRpcRequest(String methodName, IList parameters)
        {
            // Set the method name for the XML-RPC request
            MethodName = methodName;

            // Set the parameters for the XML-RPC request
            _params = parameters;
        }


        /// <summary>
        /// Sends a certificate-based XML-RPC request to the specified URL.
        /// </summary>
        /// <param name="url">The URL of the XML-RPC server.</param>
        /// <param name="certVerify">The certificate verification object.</param>
        /// <param name="checkServerCert">Whether to check the server's certificate.</param>
        /// <param name="timeout">The timeout for the request in milliseconds.</param>
        /// <returns>The XML-RPC response from the server.</returns>
        /// <exception cref="Nwc.XmlRpc.XmlRpcException">Thrown if there is an error with the request.</exception>
        public XmlRpcResponse certSend(String url, NSLCertificateVerify certVerify, bool checkServerCert, Int32 timeout)
        {
            // Log the request URL
            m_log.InfoFormat("[MONEY NSL XMLRPC]: XmlRpcResponse certSend: connect to {0}", url);

            // Create a new HTTP web request
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            if (request == null)
            {
                // Throw an exception if the request cannot be created
                throw new XmlRpcException(XmlRpcErrorCodes.TRANSPORT_ERROR, XmlRpcErrorCodes.TRANSPORT_ERROR_MSG + ": Could not create request with " + url);
            }

            // Set the request method and content type
            request.Method = "POST";
            request.ContentType = "text/xml";
            request.AllowWriteStreamBuffering = true;
            request.Timeout = timeout;
            request.UserAgent = "NSLXmlRpcRequest";

            // Add the client certificate if provided
            X509Certificate2 clientCert = null;
            if (certVerify != null)
            {
                clientCert = certVerify.GetPrivateCert();
                if (clientCert != null) request.ClientCertificates.Add(clientCert);  // Own certificate
                request.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(certVerify.ValidateServerCertificate);
            }
            else
            {
                // Disable server certificate checking if no verification object is provided
                checkServerCert = false;
            }

            // Disable server certificate checking if requested
            if (!checkServerCert)
            {
                request.Headers.Add("NoVerifyCert", "true");   // Do not verify the certificate of the other party
            }

            // Get the request stream
            Stream stream = null;
            try
            {
                stream = request.GetRequestStream();
            }
            catch (Exception ex)
            {
                // Log any errors getting the request stream
                m_log.ErrorFormat("[MONEY NSL XMLRPC]: GetRequestStream Error: {0}", ex);
                stream = null;
            }

            // Return null if the request stream could not be obtained
            if (stream == null) return null;

            // Serialize the request to the stream
            XmlTextWriter xml = new XmlTextWriter(stream, _encoding);
            _serializer.Serialize(xml, this);
            xml.Flush();
            xml.Close();

            // Get the response from the server
            HttpWebResponse response = null;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (Exception ex)
            {
                // Log any errors getting the response
                m_log.ErrorFormat("[MONEY NSL XMLRPC]: XmlRpcResponse certSend: GetResponse Error: {0}", ex.ToString());
            }
            StreamReader input = new StreamReader(response.GetResponseStream());

            string inputXml = input.ReadToEnd();
            XmlRpcResponse resp = (XmlRpcResponse)_deserializer.Deserialize(inputXml);

            // Close the input and response streams
            input.Close();
            response.Close();

            // Return the deserialized response
            return resp;
        }
    }

}
