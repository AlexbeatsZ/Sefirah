// System
global using global::System;
global using global::System.Collections;
global using global::System.Collections.Generic;
global using global::System.Collections.ObjectModel;
global using global::System.Linq;
global using global::System.Threading;
global using global::System.Threading.Tasks;
global using global::System.ComponentModel;
global using global::System.Diagnostics;
global using global::System.Text.Json;
global using global::System.Text.Json.Serialization;
global using global::System.IO;

// CommunityToolkit.Mvvm
global using global::CommunityToolkit.Mvvm.ComponentModel;
global using global::CommunityToolkit.Mvvm.DependencyInjection;
global using global::CommunityToolkit.Mvvm.Input;
global using global::CommunityToolkit.Mvvm.Messaging;

global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Localization;
global using Microsoft.Extensions.Logging;

// WinUI 3 / Windows App SDK (previously provided implicitly by the Uno.Sdk)
global using Microsoft.UI.Xaml;
global using Microsoft.UI.Xaml.Controls;
global using Microsoft.UI.Xaml.Controls.Primitives;
global using Microsoft.UI.Xaml.Data;
global using Microsoft.UI.Xaml.Input;
global using Microsoft.UI.Xaml.Media;
global using Microsoft.UI.Xaml.Media.Imaging;
global using Microsoft.UI.Xaml.Navigation;
global using Microsoft.UI.Xaml.Markup;
global using Windows.ApplicationModel;
global using Windows.Storage;

global using ApplicationExecutionState = Windows.ApplicationModel.Activation.ApplicationExecutionState;

global using global::Sefirah.Extensions;
global using global::Sefirah.Data.Contracts;
global using global::Sefirah.Data.Enums;
global using global::Sefirah.Features;
