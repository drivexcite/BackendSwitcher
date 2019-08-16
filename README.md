# BackendSwitcher

This repository contains a small .NET Core Console App prototype for Canary deployments using an API Management Gateway and three Function Apps as the backend (Green, Blue and Red) configured respectively as three API Management Gateway Backends (Production, Staging and Old), without using Deployment Slots (due to a limitation of Consumption App Service Plans).

In order to switch traffic in a controlled way, a conditional policy will route a random amount of traffic to the Staging Backend. This is to allow the Staging Function App set to become the new Production (in Consumption Plan) to gradually scale-up to the presumably high levels of traffic experienced by the Function App currently in Production. Once this gradual routing reaches some high percentage, say 90%, the code will re-arrange the backends in the API Management Gateway to completely switch traffic to the new backend by removing the conditional.

This also makes trivially easy to roll-back in case that for whatever reason, clients report errors in the newly promoted Production environment.
