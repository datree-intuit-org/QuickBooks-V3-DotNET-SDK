﻿////*********************************************************
// <copyright file="FaultHandler.cs" company="Intuit">
/*******************************************************************************
 * Copyright 2016 Intuit
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *******************************************************************************/
// <summary>This file contains SdkException.</summary>
// <summary>This file contains FaultHandler for Error responses.</summary>
////*********************************************************

using System;

namespace Intuit.Ipp.Core.Rest
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using Intuit.Ipp.Data;
    using Intuit.Ipp.Diagnostics;
    using Intuit.Ipp.Exception;
    using Intuit.Ipp.Utility;

    /// <summary>
    /// Handles the fault tags in the response and handles them.
    /// </summary>
    public class FaultHandler
    {
        /// <summary>
        /// The Service Context.
        /// </summary>
        private ServiceContext context;

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultHandler"/> class.
        /// </summary>
        /// <param name="context">The service context.</param>
        public FaultHandler(ServiceContext context)
            : this()
        {
            this.context = context;
        }

        /// <summary>
        /// Prevents a default instance of the <see cref="FaultHandler"/> class from being created.
        /// </summary>
        private FaultHandler()
        {
        }

        /// <summary>
        /// Parses the Response and throws appropriate exceptions.
        /// </summary>
        /// <param name="webException">Web Exception.</param>
        /// <param name="isIps">Specifies whether the exception is generated by an IPS call.</param>
        /// <returns>Ids Exception.</returns>
        internal IdsException ParseResponseAndThrowException(WebException webException, bool isIps = false)
        {
            IdsException idsException = null;

            // Checks whether the webException is null or not.
            if (webException != null)
            {
                // If not null then check the response property of the webException object.
                if (webException.Response != null)
                {
                    // There is a response from the Ids server. Cast it to HttpWebResponse.
                    HttpWebResponse errorResponse = (HttpWebResponse)webException.Response;

                    // Get the status code description of the error response.
                    string statusCodeDescription = errorResponse.StatusCode.ToString();

                    // Get the status code of the error response.
                    int statusCode = (int)errorResponse.StatusCode;
                    string errorString = string.Empty;

                    ICompressor responseCompressor = CoreHelper.GetCompressor(this.context, false);
                    if (!string.IsNullOrWhiteSpace(errorResponse.ContentEncoding) && responseCompressor != null)
                    {
                        using (var responseStream = errorResponse.GetResponseStream())
                        {
                            using (var decompressedStream = responseCompressor.Decompress(responseStream))
                            {
                                // Get the response stream.
                                StreamReader reader = new StreamReader(decompressedStream);

                                // Read the Stream
                                errorString = reader.ReadToEnd();
                                // Close reader
                                reader.Close();
                            }
                        }
                    }
                    else
                    {
                        using (Stream responseStream = errorResponse.GetResponseStream())
                        {
                            // Get the response stream.
                            StreamReader reader = new StreamReader(responseStream);

                            // Read the Stream
                            errorString = reader.ReadToEnd();
                            // Close reader
                            reader.Close();
                        }
                    }

                    // Log the error string to disk.
                    CoreHelper.GetRequestLogging(this.context).LogPlatformRequests(errorString, false);

                    if (isIps)
                    {
                        IdsException exception = new IdsException(errorString, statusCode.ToString(CultureInfo.InvariantCulture), webException.Source);
                        this.context.IppConfiguration.Logger.CustomLogger.Log(TraceLevel.Error, exception.ToString());
                        return exception;
                    }

                    // Use the above idsException to set to innerException of the specific exception which will be created below.

                    // Ids will set the following error codes. Depending on that we will be throwing specific exceptions.
                    switch (errorResponse.StatusCode)
                    {
                        // Bad Request: 400
                        case HttpStatusCode.BadRequest:
                            // Parse the error response and create the aggregate exception.
                            idsException = this.ParseErrorResponseAndPrepareException(errorString);
                            idsException = new IdsException(statusCodeDescription, statusCode.ToString(CultureInfo.InvariantCulture), webException.Source, idsException);
                            break;

                        // Unauthorized: 401
                        case HttpStatusCode.Unauthorized:
                            // Create Invalid Token Exception.
                            idsException = this.ParseErrorResponseAndPrepareException(errorString);
                            InvalidTokenException invalidTokenException = new InvalidTokenException(string.Format(CultureInfo.InvariantCulture, "{0}-{1}", statusCodeDescription, statusCode), idsException);
                            idsException = invalidTokenException;
                            break;

                        // ServiceUnavailable: 503
                        case HttpStatusCode.ServiceUnavailable:
                        // InternalServerError: 500
                        case HttpStatusCode.InternalServerError:
                        // Forbidden: 403
                        case HttpStatusCode.Forbidden:
                        // NotFound: 404
                        case HttpStatusCode.NotFound:
                            idsException = new IdsException(statusCodeDescription, statusCode.ToString(CultureInfo.InvariantCulture), webException.Source, new EndpointNotFoundException());
                            break;
                        // Throttle Exceeded: 429
                       case (HttpStatusCode)429:
                            idsException = new IdsException(statusCodeDescription, statusCode.ToString(CultureInfo.InvariantCulture), webException.Source, new ThrottleExceededException());
                            break;

                        // Default. Throw generic exception i.e. IdsException.
                        default:
                            // Parse the error response and create the aggregate exception.
                            // TODO: Do we need to give error string in exception also. If so then uncomemnt the below line.
                            // idsException = new IdsException(errorString, statusCode.ToString(CultureInfo.InvariantCulture), webException.Source);
                            idsException = new IdsException(statusCodeDescription, statusCode.ToString(CultureInfo.InvariantCulture), webException.Source);
                            break;
                    }
                }
            }

            // Return the Ids Exception.
            return idsException;
        }

        /// <summary>
        /// Parses the error response and prepares the response.
        /// </summary>
        /// <param name="errorString">The error string.</param>
        /// <returns>Ids Exception.</returns>
        public IdsException ParseErrorResponseAndPrepareException(string errorString)
        {
            IdsException idsException = null;

            // If the error string is null return null.
            if (string.IsNullOrWhiteSpace(errorString))
            {
                return idsException;
            }

            // Parse the xml to get the error.
            // Extract the Fault from the response.
            Fault fault = this.ExtractFaultFromResponse(errorString);

            // Iterate the Fault and Prepare the exception.
            idsException = this.IterateFaultAndPrepareException(fault);

            // return the exception.
            return idsException;
        }

        /// <summary>
        /// Extracts the Fault from the Error Response.
        /// </summary>
        /// <param name="errorString">The error string.</param>
        /// <returns>Fault object.</returns>
        private Fault ExtractFaultFromResponse(string errorString)
        {
            Fault fault = null;

            // If the error string is null return null.
            if (string.IsNullOrWhiteSpace(errorString))
            {
                return fault;
            }

            try
            {
                //// TODO: Remove this code after service returns proper response
                //// This is put in since the service is not returning proper xml header
                ////if (!errorString.StartsWith("<?xml"))
                ////{
                ////    errorString = errorString.Insert(16, " xmlns=\"http://schema.intuit.com/finance/v3\" ");
                ////    errorString = errorString.Insert(0, "<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
                ////}

                // Deserialize to IntuitResponse using the Serializer of the context.
                IntuitResponse intuitResponse = (IntuitResponse)CoreHelper.GetSerializer(this.context, false).Deserialize<IntuitResponse>(errorString);

                // Check whether the object is null or note. Also check for Items property since it has the fault.
                if (intuitResponse != null && intuitResponse.AnyIntuitObject != null)
                {
                    // TODO: Check whether only the first item will have fault or not.
                    // Cast the Items to Fault.
                    fault = intuitResponse.AnyIntuitObject as Fault;
                }

                // TODO: Uncomment this if exception has to be added for batch requests.
                // This is executed if the response is BatchItemResponse for batch requests.
                // else
                // {
                // Deserialize to BatchItemResponse using the Serializer of the context.
                //     BatchItemResponse batchItemResponse = (BatchItemResponse)this.context.Serializer.Deserialize<BatchItemResponse>(errorString);

                // Check whether the object is null or note. Also check for Item property since it has the fault.
                //     if (batchItemResponse != null && batchItemResponse.Item != null)
                //     {
                // Cast the Item to Fault.
                //         fault = batchItemResponse.Item as Fault;
                //     }
                // }
            }
            catch (System.Exception ex)
            {
                //Download might have Uri in body
                try
                {
                    if (new System.Uri(errorString) != null)
                    {
                        return null;
                    }
                }
                catch (UriFormatException)
                {
                    return null;
                }

                throw ex;
            }

            // Return the fault.
            return fault;
        }

        /// <summary>
        /// Iterates Fault and Prepares the Exception.
        /// </summary>
        /// <param name="fault">Fault object.</param>
        /// <returns>Ids exception.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic", Justification = "Require the method not to be static.")]
        private IdsException IterateFaultAndPrepareException(Fault fault)
        {
            if (fault == null)
            {
                return null;
            }

            IdsException idsException = null;

            // Create a list of exceptions.
            List<IdsError> aggregateExceptions = new List<IdsError>();

            // Check whether the fault is null or not.
            if (fault != null)
            {
                // Fault types can be of Validation, Service, Authentication and Authorization. Run them through the switch case.
                switch (fault.type)
                {
                    // If Validation errors iterate the Errors and add them to the list of exceptions.
                    case "Validation":
                    case "ValidationFault":
                        if (fault.Error != null && fault.Error.Count() > 0)
                        {
                            foreach (var item in fault.Error)
                            {
                                // Add commonException to aggregateExceptions
                                // CommonException defines four properties: Message, Code, Element, Detail.
                                aggregateExceptions.Add(new IdsError(item.Message, item.code, item.element, item.Detail));
                            }

                            // Throw specific exception like ValidationException.
                            idsException = new ValidationException(aggregateExceptions);
                        }

                        break;

                    // If Validation errors iterate the Errors and add them to the list of exceptions.
                    case "Service":
                    case "ServiceFault":
                        if (fault.Error != null && fault.Error.Count() > 0)
                        {
                            foreach (var item in fault.Error)
                            {
                                // Add commonException to aggregateExceptions
                                // CommonException defines four properties: Message, Code, Element, Detail.
                                aggregateExceptions.Add(new IdsError(item.Message, item.code, item.element, item.Detail));
                            }

                            // Throw specific exception like ServiceException.
                            idsException = new ServiceException(aggregateExceptions);
                        }

                        break;

                    // If Validation errors iterate the Errors and add them to the list of exceptions.
                    case "Authentication":
                    case "AuthenticationFault":
                    case "Authorization":
                    case "AuthorizationFault":
                        if (fault.Error != null && fault.Error.Count() > 0)
                        {
                            foreach (var item in fault.Error)
                            {
                                // Add commonException to aggregateExceptions
                                // CommonException defines four properties: Message, Code, Element, Detail.
                                aggregateExceptions.Add(new IdsError(item.Message, item.code, item.element, item.Detail));
                            }

                            // Throw specific exception like AuthenticationException which is wrapped in SecurityException.
                            idsException = new SecurityException(aggregateExceptions);
                        }

                        break;

                    // Use this as default if there was some other type of Fault
                    default:
                        if (fault.Error != null && fault.Error.Count() > 0)
                        {
                            foreach (var item in fault.Error)
                            {
                                // Add commonException to aggregateExceptions
                                // CommonException defines four properties: Message, Code, Element, Detail.
                                aggregateExceptions.Add(new IdsError(item.Message, item.code, item.element, item.Detail));
                            }

                            // Throw generic exception like IdsException.
                            idsException = new IdsException(string.Format(CultureInfo.InvariantCulture, "Fault Exception of type: {0} has been generated.", fault.type), aggregateExceptions);
                        }

                        break;
                }
            }

            // Return idsException which will be of type Validation, Service or Security.
            return idsException;
        }
    }
}
