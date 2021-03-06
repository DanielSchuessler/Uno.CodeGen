﻿// ******************************************************************
// Copyright � 2015-2018 nventive inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// ******************************************************************
using System;

namespace Uno
{
	/// <summary>
	/// Define a type (usually external) as immutable
	/// </summary>
	[System.AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
	public sealed class TreatAsImmutableAttribute : Attribute
	{
		/// <summary>
		/// The type known to be immutable
		/// </summary>
		public Type Type { get; }

		/// <summary>
		/// .ctor
		/// </summary>
		/// <param name="type"></param>
		public TreatAsImmutableAttribute(Type type)
		{
			Type = type;
		}
	}
}
