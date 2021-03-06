﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics.Contracts;
using System.Runtime.Serialization;
using System.Security.Principal;
using Revenj.DomainPatterns;
using Revenj.Extensibility;
using Revenj.Processing;
using Revenj.Security;
using Revenj.Serialization;
using Revenj.Utility;

namespace Revenj.Plugins.Server.Commands
{
	[Export(typeof(IServerCommand))]
	[ExportMetadata(Metadata.ClassType, typeof(Delete))]
	public class Delete : IServerCommand
	{
		private static Dictionary<Type, IDeleteCommand> Cache = new Dictionary<Type, IDeleteCommand>();

		private readonly IDomainModel DomainModel;
		private readonly IPermissionManager Permissions;

		public Delete(
			IDomainModel domainModel,
			IPermissionManager permissions)
		{
			Contract.Requires(domainModel != null);
			Contract.Requires(permissions != null);

			this.DomainModel = domainModel;
			this.Permissions = permissions;
		}

		[DataContract(Namespace = "")]
		public class Argument
		{
			[DataMember]
			public string Name;
			[DataMember]
			public string Uri;
		}

		private static TFormat CreateExampleArgument<TFormat>(ISerialization<TFormat> serializer)
		{
			return serializer.Serialize(new Argument { Name = "Module.AggregateRoot", Uri = "1001" });
		}

		private static TFormat CreateExampleArgument<TFormat>(ISerialization<TFormat> serializer, Type rootType)
		{
			return
				serializer.Serialize(
					new Argument
					{
						Name = rootType.FullName,
						Uri = "1001"
					});
		}

		public ICommandResult<TOutput> Execute<TInput, TOutput>(
			IServiceProvider locator,
			ISerialization<TInput> input,
			ISerialization<TOutput> output,
			IPrincipal principal,
			TInput data)
		{
			var either = CommandResult<TOutput>.Check<Argument, TInput>(input, output, data, CreateExampleArgument);
			if (either.Error != null)
				return either.Error;
			var argument = either.Argument;

			var rootType = DomainModel.Find(argument.Name);
			if (rootType == null)
				return CommandResult<TOutput>.Fail(
					"Couldn't find aggregate root type {0}.".With(argument.Name),
					@"Example argument: 
" + CommandResult<TOutput>.ConvertToString(CreateExampleArgument(output)));

			if (!typeof(IAggregateRoot).IsAssignableFrom(rootType))
				return CommandResult<TOutput>.Fail(@"Specified type ({0}) is not an aggregate root. 
Please check your arguments.".With(argument.Name), null);

			if (!Permissions.CanAccess(rootType.FullName, principal))
				return CommandResult<TOutput>.Forbidden(argument.Name);
			if (argument.Uri == null)
				return CommandResult<TOutput>.Fail(
					"Uri to delete not specified.",
					@"Example argument: 
" + CommandResult<TOutput>.ConvertToString(CreateExampleArgument(output, rootType)));

			try
			{
				IDeleteCommand command;
				if (!Cache.TryGetValue(rootType, out command))
				{
					var commandType = typeof(DeleteCommand<>).MakeGenericType(rootType);
					command = Activator.CreateInstance(commandType) as IDeleteCommand;
					var newCache = new Dictionary<Type, IDeleteCommand>(Cache);
					newCache[rootType] = command;
					Cache = newCache;
				}
				var result = command.Delete(input, output, locator, argument.Uri);

				return CommandResult<TOutput>.Success(result, "Object deleted");
			}
			catch (ArgumentException ex)
			{
				return CommandResult<TOutput>.Fail(
					ex.Message,
					ex.GetDetailedExplanation() + @"
Example argument: 
" + CommandResult<TOutput>.ConvertToString(CreateExampleArgument(output, rootType)));
			}
		}

		private interface IDeleteCommand
		{
			TOutput Delete<TInput, TOutput>(
				ISerialization<TInput> input,
				ISerialization<TOutput> output,
				IServiceProvider locator,
				string uri);
		}

		private class DeleteCommand<TRoot> : IDeleteCommand
			where TRoot : IAggregateRoot
		{
			public TOutput Delete<TInput, TOutput>(
				ISerialization<TInput> input,
				ISerialization<TOutput> output,
				IServiceProvider locator,
				string uri)
			{
				try
				{
					var repository = locator.Resolve<IPersistableRepository<TRoot>>();
					var root = repository.Find(uri);
					if (root == null)
						throw new ArgumentException("Can't find {0} with uri: {1}.".With(typeof(TRoot).FullName, uri));
					repository.Delete(root);
					return output.Serialize(root);
				}
				catch (Exception ex)
				{
					throw new ArgumentException("Error deleting: {0}.".With(ex.Message), ex);
				}
			}
		}
	}
}
