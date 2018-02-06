﻿namespace FSharp.Data.Npgsql.DesignTime

open System
open System.Data
open System.Reflection
open Npgsql

open FSharp.Quotations

open ProviderImplementation.ProvidedTypes

open FSharp.Data.Npgsql
open InformationSchema
open System.Collections.Generic

type internal RowType = {
    Provided: Type
    ErasedTo: Type
    Mapping: Expr
}

type internal ReturnType = {
    Single: Type
    PerRow: RowType option
}  with 
    member this.Row2ItemMapping = 
        match this.PerRow with
        | Some x -> x.Mapping
        | None -> Expr.Value Unchecked.defaultof<obj[] -> obj> 
    member this.SeqItemTypeName = 
        match this.PerRow with
        | Some x -> Expr.Value( x.ErasedTo.AssemblyQualifiedName)
        | None -> <@@ null: string @@>

[<AutoOpen>]
module internal Quotations = 
    let (|Arg1|) xs = 
        assert (List.length xs = 1)
        Arg1 xs.Head

    let (|Arg2|) xs = 
        assert (List.length xs = 2)
        Arg2(xs.[0], xs.[1])

    let (|Arg3|) xs = 
        assert (List.length xs = 3)
        Arg3(xs.[0], xs.[1], xs.[2])

    let (|Arg4|) xs = 
        assert (List.length xs = 4)
        Arg4(xs.[0], xs.[1], xs.[2], xs.[3])

    let (|Arg5|) xs = 
        assert (List.length xs = 5)
        Arg5(xs.[0], xs.[1], xs.[2], xs.[3], xs.[4])


type internal QuotationsFactory private() = 

    static let defaultCommandTimeout = (new NpgsqlCommand()).CommandTimeout

    [<Literal>] static let reuseDesignTimeConnectionString = "reuse design-time connection string"
    
    static member internal GetBody(methodName, specialization, [<ParamArray>] bodyFactoryArgs : obj[]) =
        
        let bodyFactory =   
            let mi = typeof<QuotationsFactory>.GetMethod(methodName, BindingFlags.NonPublic ||| BindingFlags.Static)
            assert(mi <> null)
            mi.MakeGenericMethod([| specialization |])

        fun(args : Expr list) -> 
            let parameters = Array.append [| box args |] bodyFactoryArgs
            bodyFactory.Invoke(null, parameters) |> unbox

    static member internal ToSqlParam(p : Parameter) = 

        let name = p.Name
        let dbType = p.NpgsqlDbType
        let isFixedLength = p.DataType.IsFixedLength

        <@@ 
            let x = NpgsqlParameter(name, dbType, Direction = %%Expr.Value p.Direction)

            if not isFixedLength then x.Size <- %%Expr.Value p.Size 

            x.Precision <- %%Expr.Value p.Precision
            x.Scale <- %%Expr.Value p.Scale

            x
        @@>

    static member internal MapArrayOptionItemToObj<'T>(arr, index) =
        <@
            let values : obj[] = %%arr
            values.[index] <- match unbox values.[index] with Some (x : 'T) -> box x | None -> null 
        @> 

    static member internal MapArrayObjItemToOption<'T>(arr, index) =
        <@
            let values : obj[] = %%arr
            values.[index] <- box <| if Convert.IsDBNull(values.[index]) then None else Some(unbox<'T> values.[index])
        @> 

    static member internal MapArrayNullableItems(outputColumns : Column list, mapper : string) = 
        let columnTypes, isNullableColumn = outputColumns |> List.map (fun c -> c.ClrType.FullName, c.Nullable) |> List.unzip

        QuotationsFactory.MapArrayNullableItems(columnTypes, isNullableColumn, mapper)            

    static member internal MapArrayNullableItems(columnTypes : string list, isNullableColumn : bool list, mapper : string) = 
        assert(columnTypes.Length = isNullableColumn.Length)
        let arr = Var("_", typeof<obj[]>)
        let body =
            (columnTypes, isNullableColumn) 
            ||> List.zip
            |> List.mapi(fun index (typeName, isNullableColumn) ->
                if isNullableColumn 
                then 
                    typeof<QuotationsFactory>
                        .GetMethod(mapper, BindingFlags.NonPublic ||| BindingFlags.Static)
                        .MakeGenericMethod( Type.GetType( typeName, throwOnError = true))
                        .Invoke(null, [| box(Expr.Var arr); box index |])
                        |> unbox
                        |> Some
                else 
                    None
            ) 
            |> List.choose id
            |> List.fold (fun acc x ->
                Expr.Sequential(acc, x)
            ) <@@ () @@>
        Expr.Lambda(arr, body)

    static member internal GetNullableValueFromDataRow<'T>(exprArgs : Expr list, name : string) =
        <@
            let row : DataRow = %%exprArgs.[0]
            if row.IsNull name then None else Some(unbox<'T> row.[name])
        @> 

    static member internal SetNullableValueInDataRow<'T>(exprArgs : Expr list, name : string) =
        <@
            (%%exprArgs.[0] : DataRow).[name] <- match (%%exprArgs.[1] : option<'T>) with None -> Utils.DbNull | Some value -> box value
        @> 

    static member internal GetNonNullableValueFromDataRow<'T>(exprArgs : Expr list, name: string) =
        <@ (%%exprArgs.[0] : DataRow).[name] @>

    static member internal SetNonNullableValueInDataRow<'T>(exprArgs : Expr list, name : string) =
        <@ (%%exprArgs.[0] : DataRow).[name] <- %%Expr.Coerce(exprArgs.[1], typeof<obj>) @>
    
    static member internal OptionToObj<'T> value = <@@ match %%value with Some (x : 'T) -> box x | None -> Utils.DbNull @@>    

    static member internal AddGeneratedMethod
        (sqlParameters: Parameter list, hasOutputParameters, executeArgs: ProvidedParameter list, erasedType, providedOutputType, name) =

        let mappedInputParamValues (exprArgs: Expr list) = 
            (exprArgs.Tail, sqlParameters)
            ||> List.map2 (fun expr param ->
                let value = 
                    if param.Direction = ParameterDirection.Input
                    then 
                        if param.Optional 
                        then 
                            typeof<QuotationsFactory>
                                .GetMethod("OptionToObj", BindingFlags.NonPublic ||| BindingFlags.Static)
                                .MakeGenericMethod(param.DataType.ClrType)
                                .Invoke(null, [| box expr|])
                                |> unbox
                        else
                            expr
                    else
                        let t = param.DataType.ClrType

                        if t.IsArray
                        then Expr.Value(Array.CreateInstance(t.GetElementType(), param.Size))
                        else Expr.Value(Activator.CreateInstance(t), t)

                <@@ (%%Expr.Value(param.Name) : string), %%Expr.Coerce(value, typeof<obj>) @@>
            )

        let invokeCode exprArgs =
            let methodInfo = typeof<ISqlCommand>.GetMethod(name)
            let vals = mappedInputParamValues(exprArgs)
            let paramValues = Expr.NewArray( typeof<string * obj>, elements = vals)
            if not hasOutputParameters
            then 
                Expr.Call( Expr.Coerce( exprArgs.[0], erasedType), methodInfo, [ paramValues ])    
            else
                let mapOutParamValues = 
                    let arr = Var("parameters", typeof<(string * obj)[]>)
                    let body = 
                        (sqlParameters, exprArgs.Tail)
                        ||> List.zip
                        |> List.mapi (fun index (sqlParam, argExpr) ->
                            if sqlParam.Direction.HasFlag( ParameterDirection.Output)
                            then 
                                let mi = 
                                    typeof<``ISqlCommand Implementation``>
                                        .GetMethod("SetRef")
                                        .MakeGenericMethod( sqlParam.DataType.ClrType)
                                Expr.Call(mi, [ argExpr; Expr.Var arr; Expr.Value index ]) |> Some
                            else 
                                None
                        ) 
                        |> List.choose id
                        |> List.fold (fun acc x -> Expr.Sequential(acc, x)) <@@ () @@>

                    Expr.Lambda(arr, body)

                let xs = Var("parameters", typeof<(string * obj)[]>)
                let execute = Expr.Lambda(xs , Expr.Call( Expr.Coerce( exprArgs.[0], erasedType), methodInfo, [ Expr.Var xs ]))
                <@@
                    let ps: (string * obj)[] = %%paramValues
                    let result = (%%execute) ps
                    ps |> %%mapOutParamValues
                    result
                @@>

        ProvidedMethod(name, executeArgs, providedOutputType, invokeCode)

    static member internal GetRecordType(columns: Column list) =
        columns 
            |> Seq.groupBy (fun x -> x.Name) 
            |> Seq.tryFind (fun (_, xs) -> Seq.length xs > 1)
            |> Option.iter (fun (name, _) -> failwithf "Non-unique column name %s is illegal for ResultType.Records." name)
        
        let recordType = ProvidedTypeDefinition("Record", baseType = Some typeof<obj>, hideObjectMethods = true)
        let properties, ctorParameters = 
            columns
            |> List.mapi ( fun i col ->
                let propertyName = col.Name

                if propertyName = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." (i + 1)
                    
                let propType = col.MakeProvidedType()

                let property = 
                    ProvidedProperty(
                        propertyName, 
                        propType, 
                        fun args -> <@@ %%args.[0] |> unbox |> Map.find<string, obj> propertyName @@>
                    )

                let ctorParameter = ProvidedParameter(propertyName, propType)  

                property, ctorParameter
            )
            |> List.unzip

        recordType.AddMembers properties

        let invokeCode args =
            let pairs =  
                List.zip args properties //Because we need original names in dictionary
                |> List.map (fun (arg,p) -> <@@ (%%Expr.Value(p.Name):string), %%Expr.Coerce(arg, typeof<obj>) @@>)

            <@@
                Map.ofArray<string, obj>( %%Expr.NewArray( typeof<string * obj>, pairs))
            @@> 
        
        let ctor = ProvidedConstructor(ctorParameters, invokeCode)
        recordType.AddMember ctor
        
        recordType

    static member internal GetDataRowPropertyGetterAndSetterCode (column: Column) =
        let name = column.Name
        if column.Nullable then
            let getter = QuotationsFactory.GetBody("GetNullableValueFromDataRow", column.ClrType, name)
            let setter = if column.ReadOnly then None else Some( QuotationsFactory.GetBody("SetNullableValueInDataRow", column.ClrType, name))
            getter, setter
        else
            let getter = QuotationsFactory.GetBody("GetNonNullableValueFromDataRow", column.ClrType, name)
            let setter = if column.ReadOnly then None else Some( QuotationsFactory.GetBody("SetNonNullableValueInDataRow", column.ClrType, name))
            getter, setter

    static member internal GetDataRowType (columns: Column list) = 
        let rowType = ProvidedTypeDefinition("Row", Some typeof<DataRow>)

        columns 
        |> List.mapi(fun i col ->

            if col.Name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." (i + 1)

            let propertyType = col.MakeProvidedType()

            let getter, setter = QuotationsFactory.GetDataRowPropertyGetterAndSetterCode col

            let p = ProvidedProperty(col.Name, propertyType, getter, ?setterCode = setter)

            if col.Description <> "" then p.AddXmlDoc col.Description

            p
        )
        |> rowType.AddMembers

        rowType

    static member internal GetDataTableType
        (
            typeName, 
            dataRowType: ProvidedTypeDefinition, 
            outputColumns: Column list, 
            selectCommand: Expr<NpgsqlCommand>, 
            allowDesignTimeConnectionStringReUse: bool,
            ?connectionString: string
        ) =

        let tableType = ProvidedTypeDefinition(typeName, Some typeof<DataTable>)
      
        do //Columns
            let columnsType = ProvidedTypeDefinition("Columns", Some typeof<DataColumnCollection>)
            tableType.AddMember columnsType
            let columns = ProvidedProperty("Columns", columnsType, getterCode = fun args -> <@@ (%%args.Head: DataTable).Columns @@>)
            tableType.AddMember columns
      
            for column in outputColumns do
                let propertyType = ProvidedTypeDefinition(column.Name, Some typeof<DataColumn>)
                columnsType.AddMember propertyType

                let property = 
                    let columnName = column.Name
                    ProvidedProperty(column.Name, propertyType, getterCode = fun args -> <@@ (%%args.Head: DataColumnCollection).[columnName] @@>)
            
                columnsType.AddMember property

        let rowsType = ProvidedTypeDefinition("Rows", None)
        tableType.AddMember rowsType

        do //Rows
            let rows = ProvidedProperty("Rows", rowsType, getterCode = fun args -> <@@ (%%args.Head: DataTable).Rows @@>)
            tableType.AddMember rows

        do
            let parameters, updateableColumns = 
                [ 
                    for c in outputColumns do 
                        if not(c.Identity || c.ReadOnly)
                        then 
                            let dataType = c.MakeProvidedType(forceNullability = c.OptionalForInsert)
                            let parameter = 
                                if c.OptionalForInsert
                                then ProvidedParameter(c.Name, parameterType = dataType, optionalValue = null)
                                else ProvidedParameter(c.Name, dataType)

                            yield parameter, c
                ] 
                |> List.sortBy (fun (_, c) -> c.OptionalForInsert) //move non-nullable params in front
                |> List.unzip

            let methodXmlDoc = 
                String.concat "\n" [
                    for c in updateableColumns do
                        if c.Description <> "" 
                        then 
                            let defaultConstrain = 
                                if c.HasDefaultConstraint 
                                then sprintf " Default constraint: %s." c.DefaultConstraint
                                else ""
                            yield sprintf "<param name='%s'>%s%s</param>" c.Name c.Description defaultConstrain
                ]


            let invokeCode = fun (args: _ list)-> 

                let argsValuesConverted = 
                    (args.Tail, updateableColumns)
                    ||> List.map2 (fun valueExpr c ->
                        if c.OptionalForInsert
                        then 
                            typeof<``ISqlCommand Implementation``>
                                .GetMethod("OptionToObj", BindingFlags.NonPublic ||| BindingFlags.Static)
                                .MakeGenericMethod(c.ClrType)
                                .Invoke(null, [| box valueExpr |])
                                |> unbox
                        else
                            valueExpr
                    )

                <@@ 
                    let table: DataTable = %%args.[0]
                    let row = table.NewRow()

                    let values: obj[] = %%Expr.NewArray(typeof<obj>, [ for x in argsValuesConverted -> Expr.Coerce(x, typeof<obj>) ])
                    let namesOfUpdateableColumns: string[] = %%Expr.NewArray(typeof<string>, [ for c in updateableColumns -> Expr.Value(c.Name) ])
                    let optionalParams: bool[] = %%Expr.NewArray(typeof<bool>, [ for c in updateableColumns -> Expr.Value(c.OptionalForInsert) ])

                    for name, value, optional in Array.zip3 namesOfUpdateableColumns values optionalParams do 
                        row.[name] <- if value = null && optional then Utils.DbNull else value
                    row
                @@>

            do 
                let newRowMethod = ProvidedMethod("NewRow", parameters, dataRowType, invokeCode)
                if methodXmlDoc <> "" then newRowMethod.AddXmlDoc methodXmlDoc
                tableType.AddMember newRowMethod

                let addRowMethod =
                    ProvidedMethod(
                        "AddRow", 
                        parameters, 
                        typeof<Void>, 
                        invokeCode = fun args ->
                            let newRow = invokeCode args
                            <@@
                                let table: DataTable = %%args.[0]
                                let row: DataRow = %%newRow
                                table.Rows.Add row
                            @@>
                    )

                if methodXmlDoc <> "" then addRowMethod.AddXmlDoc methodXmlDoc
                tableType.AddMember addRowMethod

        do
            let commonParams = [
                ProvidedParameter("updateBatchSize", typeof<int>, optionalValue = 1) 
                ProvidedParameter("continueUpdateOnError", typeof<bool>, optionalValue = false) 
                ProvidedParameter("conflictOption", typeof<ConflictOption>, optionalValue = ConflictOption.CompareAllSearchableValues) 
            ]

            let designTimeConnectionString = defaultArg connectionString ""

            tableType.AddMembers [

                ProvidedMethod(
                    "Update", 
                    ProvidedParameter("connectionString", typeof<string>, ?optionalValue = (connectionString |> Option.map (fun _ -> box reuseDesignTimeConnectionString))) :: commonParams, 
                    typeof<int>,
                    fun (Arg5(table, connectionString, updateBatchSize, continueUpdateOnError, conflictOption)) -> 
                        <@@ 
                            let selectCommand = %selectCommand
                            let runTimeConnectionString = 
                                if %%connectionString = reuseDesignTimeConnectionString
                                then 
                                    if allowDesignTimeConnectionStringReUse 
                                    then designTimeConnectionString
                                    else failwith Const.prohibitDesignTimeConnStrReUse
                                else
                                    %%connectionString

                            selectCommand.Connection <- new NpgsqlConnection(runTimeConnectionString)
                            Utils.updateDataTable(%%table, selectCommand, %%updateBatchSize, %%continueUpdateOnError, %%conflictOption)
                        @@>
                )

                ProvidedMethod(
                    "Update", 
                    ProvidedParameter("transaction", typeof<NpgsqlTransaction> ) :: commonParams, 
                    typeof<int>,
                    fun (Arg5(table, tx, updateBatchSize, continueUpdateOnError,conflictOption)) -> 
                        <@@ 
                            let selectCommand = %selectCommand
                            selectCommand.Transaction <- %%tx
                            selectCommand.Connection <- selectCommand.Transaction.Connection
                            Utils.updateDataTable(%%table, selectCommand, %%updateBatchSize, %%continueUpdateOnError, %%conflictOption)
                        @@>
                )

            ]

            do 
                rowsType.AddMembersDelayed <| fun() -> 
                    [
                        ProvidedProperty(
                            "Item", 
                            dataRowType, 
                            getterCode = (fun args -> <@@ (%%Expr.Coerce(args.[0], typeof<DataRowCollection>): DataRowCollection).[%%args.[1]] @@>),
                            setterCode = (fun args -> <@@ (%%Expr.Coerce(args.[0], typeof<DataRowCollection>): DataRowCollection).[%%args.[1]] = %%args.[2] @@>),
                            indexParameters = [ ProvidedParameter("index", typeof<int>) ]
                        )

                        ProvidedProperty("Count", typeof<int>,
                            fun args -> <@@ (%%Expr.Coerce(args.[0], typeof<DataRowCollection>): DataRowCollection).Count @@>
                        )
                    ]

            do 
                rowsType.AddMembersDelayed <| fun() -> 
                    [
                        ProvidedMethod("Add", [ ProvidedParameter("row", dataRowType) ], typeof<Void>, 
                            fun (Arg2(xs, row)) -> <@@ (%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).Add(%%row: DataRow) @@> 
                        )
                
                        ProvidedMethod("Clear", [], typeof<Void>, 
                            fun (Arg1 xs) -> <@@(%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).Clear() @@> 
                        )
                
                        //ProvidedMethod("Contains", [ ProvidedParameter("keys", typeof<obj[]>, IsParamArray = true) ], typeof<bool>, 
                        //    fun (Arg2(xs, keys)) -> <@@(%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).Contains(%%keys: obj[]) @@> 
                        //)

                        //ProvidedMethod("CopyTo", [ ProvidedParameter("array", dataRowType.MakeArrayType()); ProvidedParameter("index", typeof<int>) ], typeof<Void>, 
                        //    fun (Arg3(xs, array, index))-> <@@ (%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).CopyTo(%%array, %%index) @@> 
                        //)
                
                        //ProvidedMethod("Find", [ ProvidedParameter("keys", typeof<obj[]>, IsParamArray = true) ], dataRowType, 
                        //    fun (Arg2(xs, keys)) -> <@@(%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).Find(%%keys: obj[]) @@> 
                        //)

                        ProvidedMethod("GetEnumerator", [], ProvidedTypeBuilder.MakeGenericType(typedefof<_ seq>, [dataRowType]),
                            fun (Arg1 xs) -> <@@ (%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection) |> Seq.cast<DataRow>  @@> 
                        )

                        ProvidedMethod("IndexOf", [ ProvidedParameter("row", dataRowType) ], typeof<Void>, 
                            fun (Arg2(xs, row)) -> <@@ (%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).IndexOf(%%row: DataRow) @@> 
                        )

                        ProvidedMethod("InsertAt", [ ProvidedParameter("row", dataRowType); ProvidedParameter("index", typeof<int>) ], typeof<Void>, 
                            fun (Arg3(xs, row, index))-> <@@ (%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).InsertAt(%%row, %%index) @@> 
                        )
                
                        ProvidedMethod("Remove", [ ProvidedParameter("row", dataRowType) ], typeof<Void>, 
                            fun (Arg2(xs, row)) -> <@@ (%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).Remove(%%row) @@> 
                        )
                
                        ProvidedMethod("RemoveAt", [ ProvidedParameter("index", typeof<int>) ], typeof<Void>, 
                            fun (Arg2(xs, index)) -> <@@ (%%Expr.Coerce(xs, typeof<DataRowCollection>): DataRowCollection).RemoveAt(%%index) @@> 
                        )
                    ] 

        tableType

    static member internal GetOutputTypes(outputColumns, resultType, commandBehaviour: CommandBehavior, commandText, hasOutputParameters, allowDesignTimeConnectionStringReUse, ?connectionString) =    
         
        if resultType = ResultType.DataReader 
        then 
            { Single = typeof<NpgsqlDataReader>; PerRow = None }
        elif List.isEmpty outputColumns
        then 
            { Single = typeof<int>; PerRow = None }
        elif resultType = ResultType.DataTable 
        then
            let dataRowType = QuotationsFactory.GetDataRowType(outputColumns)
            let dataTableType = 
                QuotationsFactory.GetDataTableType(
                    "Table", 
                    dataRowType, 
                    outputColumns, 
                    <@ new NpgsqlCommand(commandText) @>, 
                    allowDesignTimeConnectionStringReUse,
                    ?connectionString = connectionString
                )

            dataTableType.AddMember dataRowType

            { Single = dataTableType; PerRow = None }

        else 
            let providedRowType, erasedToRowType, rowMapping = 
                if List.length outputColumns = 1
                then
                    let column0 = outputColumns.Head
                    let erasedTo = column0.ClrTypeConsideringNullability
                    let provided = column0.MakeProvidedType()
                    let values = Var("values", typeof<obj[]>)
                    let indexGet = Expr.Call(Expr.Var values, typeof<Array>.GetMethod("GetValue", [| typeof<int> |]), [ Expr.Value 0 ])
                    provided, erasedTo, Expr.Lambda(values,  indexGet) 

                elif resultType = ResultType.Records 
                then 
                    let provided = QuotationsFactory.GetRecordType(outputColumns)
                    let names = Expr.NewArray(typeof<string>, outputColumns |> List.map (fun x -> Expr.Value(x.Name))) 
                    let mapping = <@@ fun (values: obj[]) -> ((%%names: string[]), values) ||> Array.zip |> Map.ofArray |> box @@>

                    upcast provided, typeof<obj>, mapping
                else 
                    let providedType = 
                        match outputColumns with
                        | [ x ] -> x.MakeProvidedType()
                        | xs -> Reflection.FSharpType.MakeTupleType [| for x in xs -> x.MakeProvidedType() |]

                    let erasedToTupleType = 
                        match outputColumns with
                        | [ x ] -> x.ClrTypeConsideringNullability
                        | xs -> Reflection.FSharpType.MakeTupleType [| for x in xs -> x.ClrTypeConsideringNullability |]

                    let clrTypeName = erasedToTupleType.FullName
                    let mapping = <@@ Reflection.FSharpValue.PreComputeTupleConstructor( Type.GetType( clrTypeName, throwOnError = true))  @@>
                    providedType, erasedToTupleType, mapping
            
            let nullsToOptions = QuotationsFactory.MapArrayNullableItems(outputColumns, "MapArrayObjItemToOption") 
            
            { 
                Single = 
                    if commandBehaviour.HasFlag(CommandBehavior.SingleRow)  
                    then
                        ProvidedTypeBuilder.MakeGenericType(typedefof<_ option>, [ providedRowType ])
                    else
                        let collectionType = if hasOutputParameters then typedefof<_ list> else typedefof<_ seq>
                        ProvidedTypeBuilder.MakeGenericType( collectionType, [ providedRowType ])

                PerRow = Some { 
                    Provided = providedRowType
                    ErasedTo = erasedToRowType
                    Mapping = <@@ Utils.getMapperWithNullsToOptions(%%nullsToOptions, %%rowMapping) @@> 
                }               
            }

    static member internal GetExecuteArgs(sqlParameters: Parameter list, customType: IDictionary<_, ProvidedTypeDefinition>) = 
        [
            for p in sqlParameters do
                //assert p.Name.StartsWith("@")
                //let parameterName = p.Name.Substring 1
                let parameterName = p.Name

                let t = 
                    if p.DataType.IsUserDefinedType
                    then
                        let t = customType.[ p.DataType.UdtTypeName ] 
                        if p.DataType.ClrType.IsArray 
                        then t.MakeArrayType()
                        else upcast t
                    else p.DataType.ClrType

                if p.Optional 
                then 
                    assert(p.Direction = ParameterDirection.Input)
                    yield ProvidedParameter(
                        parameterName, 
                        parameterType = ProvidedTypeBuilder.MakeGenericType( typedefof<_ option>, [ t ]), 
                        optionalValue = null
                    )
                else
                    if p.Direction.HasFlag(ParameterDirection.Output)
                    then
                        yield ProvidedParameter(parameterName, parameterType = t.MakeByRefType(), isOut = true)
                    else                                 
                        yield ProvidedParameter(parameterName, parameterType = t)
        ]

    static member internal GetCommandCtors
        (
            cmdProvidedType: ProvidedTypeDefinition, 
            designTimeConfig, 
            allowDesignTimeConnectionStringReUse: bool,            
            ?connectionString: string, 
            ?factoryMethodName
        ) = 

        [
            let ctorImpl = typeof<``ISqlCommand Implementation``>.GetConstructors() |> Array.exactlyOne

            let parameters1 = [ 
                ProvidedParameter(
                    "connectionString", 
                    typeof<string>, 
                    ?optionalValue = (connectionString |> Option.map (fun _ -> box reuseDesignTimeConnectionString))
                ) 
                ProvidedParameter("commandTimeout", typeof<int>, optionalValue = defaultCommandTimeout) 
            ]

            let body1 (args: _ list) = 
                let designTimeConnectionString = defaultArg connectionString ""
                let runTimeConnectionString = 
                    <@@
                        if %%args.Head = reuseDesignTimeConnectionString
                        then 
                            if allowDesignTimeConnectionStringReUse 
                            then designTimeConnectionString
                            else failwith Const.prohibitDesignTimeConnStrReUse
                        else
                            %%args.Head
                    @@>

                Expr.NewObject(ctorImpl, designTimeConfig :: <@@ Choice<string, NpgsqlTransaction>.Choice1Of2 %%runTimeConnectionString @@> :: args.Tail)

            yield ProvidedConstructor(parameters1, invokeCode = body1) :> MemberInfo
            
            if factoryMethodName.IsSome
            then 
                yield upcast ProvidedMethod(factoryMethodName.Value, parameters1, returnType = cmdProvidedType, invokeCode = body1, isStatic = true)
           
            let parameters2 = 
                    [ 
                        ProvidedParameter("transaction", typeof<NpgsqlTransaction>) 
                        ProvidedParameter("commandTimeout", typeof<int>, optionalValue = defaultCommandTimeout) 
                    ]

            let body2 (args: _ list) = Expr.NewObject(ctorImpl, designTimeConfig :: <@@ Choice<string, NpgsqlTransaction>.Choice2Of2 %%args.Head @@> :: args.Tail )
                    
            yield upcast ProvidedConstructor(parameters2, invokeCode = body2)
            if factoryMethodName.IsSome
            then 
                yield upcast ProvidedMethod(factoryMethodName.Value, parameters2, returnType = cmdProvidedType, invokeCode = body2, isStatic = true)
        ]


