# CryptoHook

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)

---

### ✨ Project Status: Low Maintenance & Community Driven

**This project is currently in a low-maintenance mode, and community contributions are welcome!**

While I have limited time to dedicate to developing new features myself, I am happy to review and merge pull requests for bug fixes and new features.

Please be patient, as review times may vary, but I will do my best to get to all contributions. Your support and collaboration are greatly appreciated in helping CryptoHook grow!

---

## Overview

CryptoHook is an open-source, self-hosted Cryptocurrency Payment API. It was designed with a focus on privacy and giving users full control over their payment infrastructure.

The existing code is from an early development stage and should be considered a prototype. It is not recommended for production use.

## Original Vision & Potential Features

The following list outlines the features and improvements that were originally planned for CryptoHook. This can serve as inspiration or a starting point for anyone interested in contributing to the project.

- [ ] **Broader Cryptocurrency Support**: Extend beyond the initial set of currencies.
- [ ] **CI/CD Integration**: Implement a pipeline for automated testing and deployment.
- [ ] **Architectural Shift**: Transition from REST to RPC for improved performance (for checking for payments on the blockchain).
- [ ] **Enhanced Security**: Implement more robust authentication and security hardening.
- [ ] **Docker Support**: Provide a `Dockerfile` for easy, containerized deployment.
- [ ] **Expanded Documentation**: Comprehensive setup guides, API references, and examples.

## Getting Started

1.  Clone the repository:
    ```sh
    git clone [https://github.com/byayex/CryptoHook.git](https://github.com/byayex/CryptoHook.git)
    ```
2.  Open the solution in your preferred .NET IDE (e.g., Visual Studio, JetBrains Rider). **|** I recommend using VS Code and opening the Workspace in the root directory
3.  Review the `CryptoHook.Api` and unit test projects to understand the basic structure.

## Contributing

Contributions are the primary way this project will move forward! If you have a bug fix or a new feature, please feel free to:

1.  **Fork the repository.**
2.  Create your feature branch (`git checkout -b feature/AmazingFeature`).
3.  Commit your changes (`git commit -m 'Add some AmazingFeature'`).
4.  Push to the branch (`git push origin feature/AmazingFeature`).
5.  **Open a Pull Request.**

I will review all PRs and provide feedback as my time allows. Thank you for helping out!

## License

This project is licensed under the [GNU GPLv3 License](https://www.gnu.org/licenses/gpl-3.0).
