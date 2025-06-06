// <copyright file="CommandEventArgs.cs" company="Rnwood.SmtpServer project contributors">
// Copyright (c) Rnwood.SmtpServer project contributors. All rights reserved.
// Licensed under the BSD license. See LICENSE.md file in the project root for full license information.
// </copyright>

using System;

namespace Rnwood.SmtpServer;

/// <summary>
///     Defines the <see cref="CommandEventArgs" />.
/// </summary>
public class CommandEventArgs : EventArgs
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandEventArgs" /> class.
    /// </summary>
    /// <param name="command">The command<see cref="SmtpCommand" />.</param>
    public CommandEventArgs(SmtpCommand command, ISession session, IConnection connection)
    {
        this.Command = command;
        this.Session = session;
        this.Connection = connection;
    }

    /// <summary>
    ///     Gets the Command.
    /// </summary>
    public SmtpCommand Command { get; private set; }

    public ISession Session { get; private set; }

    public IConnection Connection { get; private set; }
}
