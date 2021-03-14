# Welcome to ikspōz

Welcome to ikspōz, a web traffic tunneling tool aimed at allowing developers to
easily leverage an existing Azure Subscription to effortlessly tunnel traffic
from external clients/services into your local network.

## What does this thing do?

ikspōz aims to provide a very simple way to leverage an existing Azure
Subscription by utilizing Azure Relay to create a Hybrid Connection that can
tunnel the traffic between a developer's local network and an endpoint on the
public internet.

### Use cases

The primary use cases for a tool like this are:

1. You are working on a project that you want to expose from your local machine
   to the public internal so other people can access it.
1. You are integrating with an external web service that issues callbacks in
   the form of web hooks and you need those directed to your local machine so
   you can debug or test your working version of the code.

## How do I get started?

### Installing

Initially we're offering the program as [a .NET tool](https://docs.microsoft.com/en-us/dotnet/core/tools/global-tools)
because we expect the initial audience to be developers who will already have
the .NET SDK installed.

It can be installed using the following:

> dotnet tool install HackedBrain.ikspoz.cli

If there is enough interest for other kinds of packages/installers we will look
into creating those. This would be provided as standalone installs and would not
require the .NET SDK to be installed. Please use [the
`installation`
label](https://github.com/HackedBrain/ikspoz/issues?q=is%3Aissue+label%3Ainstallation)
to find existing issues or for creating new issues related to installation.

You can also [keep an eye on the releases page](https://github.com/HackedBrain/ikspoz/releases) for release notes or
to eventually download the latest versions of whatever standalone installs we
provide.

### Contributing

If you're interested in contributing [please check out our documentation on contributing here](./CONTRIBUTING.md).

## FAQ

### What's with the name?

The name comes from the phonetic pronunciation of the English word "expose".

### What are project's goals?

- A tool for developers
- CLI first
- Cross platform
- Easily installed via well known application management tools
- Web traffic focused (HTTP/1+2 today, WebSockets coming)
- Direct integration with Azure that require nothing more than an Azure
  Subscription to get started
- Ability to connect to existing Azure Relay instances managed outside of
  the tool

### What are the project's non-goals?

- Support for non-"web" traffic (e.g. pure TCP connections)
- Running as part of any system infrastructure

### How much does this cost?

The ikspōz tool itself is provided for free.

#### Azure Relay mode costs

When using the direct Azure Relay mode [there are costs
associated with an Azure Relay instance](https://docs.microsoft.com/en-us/azure/azure-relay/relay-faq#pricing). You will be billed via your Azure Subscription for those
charges and ikspōz has nothing to do with those.

### I noticed you work at Microsoft, is this a Microsoft supported tool?

No. This should be considered a personal OSS project and you should expect
_zero_ support from Microsoft for this tool.

### Do you know there are already other tools out there that do this?

Yes, however there are several reasons why those tools may not work well for
some people and that's why this is being created as an alternative. You are free
to compare and contrast and make a decision which one best suits your needs.

> NOTE: If you don't already have an Azure account, you can get always started
> with a free account, just [check out the FAQ here](https://azure.microsoft.com/en-us/free/free-account-faq/).
