﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;

namespace Nethermind.Core.Logging
{
    /// <summary>
    /// Logger for testing where we want to test whether any test message creation would cause an exception
    /// </summary>
    public class LimboTraceLogger : ILogger
    {
        private static LimboTraceLogger _instance;
        
        public static LimboTraceLogger Instance
        {
            get { return LazyInitializer.EnsureInitialized(ref _instance, () => new LimboTraceLogger()); }
        }
        
        public void Info(string text)
        {
        }

        public void Warn(string text)
        {
        }

        public void Debug(string text)
        {
        }

        public void Trace(string text)
        {
        }

        public void Error(string text, Exception ex = null)
        {
        }

        public bool IsInfo => true;
        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsTrace => true;
        public bool IsError => true;
    }
}