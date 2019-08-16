using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;

namespace BackendSwitcher
{
    class Switcher
    {
        public static HttpClient HttpClient { get; } = new HttpClient();

        public static async Task<string> GetCurrentPolicy(AzureApiManagementGatewayConfiguration config, string jwt, string apiName)
        {
            var getPolicyUrl = $"https://management.azure.com/subscriptions/{config.SubscriptionId}/resourceGroups/{config.ResourceGroupName}/providers/Microsoft.ApiManagement/service/{config.ApiManagementGatewayName}/apis/{apiName}/policies/policy?api-version=2019-01-01";

            using (var request = new HttpRequestMessage { RequestUri = new Uri(getPolicyUrl) })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

                using (var response = await HttpClient.SendAsync(request))
                {
                    return response.IsSuccessStatusCode ? (await response.Content.ReadAsStringAsync()) : null;
                }
            }
        }

        public static async Task<string> GetJwt(AzureProviderAuthorization authorizationInfo)
        {
            var requestUrl = authorizationInfo.AuthorizationServiceUrl.Replace("{{tenantId}}", authorizationInfo.TenandId);
            var postBody = $"grant_type=client_credentials&client_id={authorizationInfo.ClientId}&client_secret={authorizationInfo.ClientSecret}&resource=https://management.azure.com/";

            using (var request = new HttpRequestMessage { RequestUri = new Uri(requestUrl), Content = new StringContent(postBody, Encoding.UTF8, "application/x-www-form-urlencoded") })
            using (var response = await HttpClient.SendAsync(request))
            {
                if (!response.IsSuccessStatusCode)
                    return null;

                var responseText = await response.Content.ReadAsStringAsync();
                var jsonResponse = JToken.Parse(responseText);

                return jsonResponse["access_token"].Value<string>();
            }
        }

        public static string GetExistingBackendId(string currentPolicyXml)
        {
            var xml = XDocument.Parse(currentPolicyXml);
            return xml.XPathSelectElement("/policies/inbound/set-backend-service[@id='apim-generated-policy']")?.Attribute("backend-id").Value
                ?? xml.XPathSelectElement("/policies/inbound/choose/otherwise/set-backend-service")?.Attribute("backend-id").Value;
        }

        public static string GetCanaryPolicy(string currentPolicyXml, int canaryPercentage, string productionBackendId, string canaryBackendId)
        {
            var inboundContent = $@"
<choose>
    <when condition=""@(new Random().Next(100) &lt; {canaryPercentage})"">
        <set-backend-service backend-id=""{canaryBackendId}""/>
    </when>
    <otherwise>
        <set-backend-service id=""apim-generated-policy"" backend-id=""{productionBackendId}"" />
    </otherwise>
</choose>
";

            var xml = XDocument.Parse(currentPolicyXml);
            var element = xml.XPathSelectElement("/policies/inbound");

            var replacementNode = (from child in element.Descendants() where child.Name.LocalName == "choose" select child).FirstOrDefault()
                ?? (from child in element.Descendants() where child.Name.LocalName == "set-backend-service" select child).FirstOrDefault();

            replacementNode.Remove();
            element.Add(XDocument.Parse(inboundContent).Root);

            return xml.ToString();
        }

        public static string GetSingleBackendPolicy(string currentPolicyXml, string productionBackendId)
        {
            var inboundContent = $@"<set-backend-service id=""apim-generated-policy"" backend-id=""{productionBackendId}"" />";

            var xml = XDocument.Parse(currentPolicyXml);
            var element = xml.XPathSelectElement("/policies/inbound");

            var replacementNode = (from child in element.Descendants() where child.Name.LocalName == "choose" select child).FirstOrDefault()
                ?? (from child in element.Descendants() where child.Name.LocalName == "set-backend-service" select child).FirstOrDefault();

            replacementNode.Remove();

            var setBackendNode = XDocument.Parse($"<a>{inboundContent}</a>");
            element.Add(setBackendNode.Root.Elements().FirstOrDefault());

            return xml.ToString();
        }

        public static async Task<bool> SetPolicy(AzureApiManagementGatewayConfiguration config, string jwt, string apiName, string xmlPolicy)
        {
            var url = $"https://management.azure.com/subscriptions/{config.SubscriptionId}/resourceGroups/{config.ResourceGroupName}/providers/Microsoft.ApiManagement/service/{config.ApiManagementGatewayName}/apis/{apiName}/policies/policy?api-version=2019-01-01";
            var requestBody = JsonConvert.SerializeObject(new
            {
                properties = new
                {
                    format = "xml",
                    value = xmlPolicy
                }
            });

            using (var request = new HttpRequestMessage { RequestUri = new Uri(url), Method = HttpMethod.Put, Content = new StringContent(requestBody, Encoding.UTF8, "application/json") })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

                using (var response = await HttpClient.SendAsync(request))
                {
                    return response.IsSuccessStatusCode;
                }
            }
        }

        static void Main(string[] args)
        {
            var authotizationInfo = new AzureProviderAuthorization
            {
                AuthorizationServiceUrl = "https://login.microsoftonline.com/{{tenantId}}/oauth2/token",
                TenandId = "<From Azure AD>",
                ClientId = "<From RBAC Service Principal AppId>",
                ClientSecret = "<From RBAC Service Principal>"
            };

            var jwt = GetJwt(authotizationInfo).Result;

            var apimConfiguration = new AzureApiManagementGatewayConfiguration
            {
                ApiManagementGatewayName = "hwt-apim-westus-triage",
                ResourceGroupName = "hwt-rg-westus-triage",
                SubscriptionId = "<From Azure Portal Subscription Id>"
            };

            var productionApiName = "hwt-api-westus-triage";
            var stagingApiName = "hwt-api-stage-westus-triage";
            var oldApiName = "hwt-api-old-westus-triage";

            var productionApiPolicyXml = GetCurrentPolicy(apimConfiguration, jwt, productionApiName).Result;
            var stagingApiPolicyXml = GetCurrentPolicy(apimConfiguration, jwt, stagingApiName).Result;
            var oldApiPolicyXml = GetCurrentPolicy(apimConfiguration, jwt, oldApiName).Result;

            var productionBackendId = GetExistingBackendId(productionApiPolicyXml);
            var stagingBackendId = GetExistingBackendId(stagingApiPolicyXml);
            var oldBackendId = GetExistingBackendId(oldApiPolicyXml);

            // Iterate over a period of time and increase the percentage of traffic to the new backend.
            var newCanaryPolicy = GetCanaryPolicy(productionApiPolicyXml, 70, productionBackendId, stagingBackendId);
            var setCanaryPolicy = SetPolicy(apimConfiguration, jwt, productionApiName, newCanaryPolicy).Result;

            // At the end of the process, rearrange the pointers so that: 
            // 1) The Staging gets promoted to Production.
            // 2) Production gets demoted to Old.
            // 3) Old gets switched to Staging.

            var newPolicyForProduction = GetSingleBackendPolicy(productionApiPolicyXml, stagingBackendId);
            var newPolicyForOld = GetSingleBackendPolicy(oldApiPolicyXml, productionBackendId);
            var newPolicyForStaging = GetSingleBackendPolicy(stagingApiPolicyXml, oldBackendId);

            var setProductionPolicy = SetPolicy(apimConfiguration, jwt, productionApiName, newPolicyForProduction).Result;
            var setStagingPolicy = SetPolicy(apimConfiguration, jwt, stagingApiName, newPolicyForStaging).Result;
            var setOldPolicy = SetPolicy(apimConfiguration, jwt, oldApiName, newPolicyForOld).Result;
        }
    }
}
